// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #108. A finished programme is finite, which is exactly where it differs from the live
// tuner's media source. Copying the live shape would give a stream with no end and no runtime,
// and a client with no seek bar.

using System;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class CatchupMediaSourceTests
{
    private static readonly CatchupItemRef OneHour =
        new(CatchupItemKind.Programme, 0, 4711, 0, 1_700_000_000, 1_700_003_600);

    [Fact]
    public void ItIsNotAnInfiniteStream()
    {
        // The live tuner sets this true. A recording ends.
        XtreamCatchupChannel.BuildMediaSource("id", "http://host/x.ts", OneHour)
            .IsInfiniteStream.Should().BeFalse();
    }

    [Fact]
    public void TheRuntimeComesFromTheProgrammesOwnWindow()
    {
        // Without a runtime a client cannot draw a seek bar, which is most of the point of being
        // able to watch something that already aired.
        XtreamCatchupChannel.BuildMediaSource("id", "http://host/x.ts", OneHour)
            .RunTimeTicks.Should().Be(TimeSpan.FromHours(1).Ticks);
    }

    [Fact]
    public void ItCarriesNoPlaceholderStreams()
    {
        // The live path invents a video and an audio entry to skip probing. Doing that here makes
        // Jellyfin skip the probe as well, and a finite file is cheap to probe properly.
        var source = XtreamCatchupChannel.BuildMediaSource("id", "http://host/x.ts", OneHour);
        source.MediaStreams.Should().BeNullOrEmpty();
        source.SupportsProbing.Should().BeTrue();
    }

    [Fact]
    public void ItIsServedOverHttpAsTransportStream()
    {
        var source = XtreamCatchupChannel.BuildMediaSource("id", "http://host/x.ts", OneHour);
        source.Protocol.Should().Be(MediaProtocol.Http);
        source.Container.Should().Be("ts");
        source.IsRemote.Should().BeTrue();
    }

    [Fact]
    public void TheSourceIsIdentifiedByTheItemItBelongsTo()
    {
        XtreamCatchupChannel.BuildMediaSource("xcu|p|0|4711|1|2", "http://host/x.ts", OneHour)
            .Id.Should().Be("xcu|p|0|4711|1|2");
    }

    [Fact]
    public void DashUsesTheManifestDemuxerAndKeepsTheProgrammeDuration()
    {
        var source = XtreamCatchupChannel.BuildMediaSource("xcu|p|0|4711|1|2", "http://host/archive.mpd", OneHour);
        source.Container.Should().Be("dash");
        source.IsInfiniteStream.Should().BeFalse();
        source.RunTimeTicks.Should().Be(TimeSpan.FromHours(1).Ticks);
        source.MediaStreams.Should().BeNullOrEmpty();
    }

    [Fact]
    public void BrowsingKeepsAStaticPlaceholderUntilThePlaybackUrlIsResolved()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(OneHour.StartUnix);
        var stop = DateTimeOffset.FromUnixTimeSeconds(OneHour.StopUnix);
        var item = XtreamCatchupChannel.BuildItem(OneHour, start, stop, "Programme", null);
        item.MediaSources.Should().ContainSingle().Which.Type.Should().Be(MediaSourceType.Placeholder);
        item.MediaSources[0].Path.Should().BeNullOrEmpty();
        item.DateModified.Should().BeAfter(DateTime.MinValue);
        item.RunTimeTicks.Should().Be(TimeSpan.FromHours(1).Ticks);
    }

    [Fact]
    public void ParseHlsMasterMediaSource_ParsesHevcAndAudioTracksForChasePlay()
    {
        const string master = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-INDEPENDENT-SEGMENTS
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="Slovak",LANGUAGE="slk",DEFAULT=YES,AUTOSELECT=YES,URI="http://127.0.0.1:5050/hls/abc/audio/0.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="Original",LANGUAGE="mul",DEFAULT=NO,AUTOSELECT=YES,URI="http://127.0.0.1:5050/hls/abc/audio/1.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=3400000,RESOLUTION=1280x720,CODECS="hvc1.1.6.L120.b0,mp4a.40.2",AUDIO="audio"
            http://127.0.0.1:5050/hls/abc/video/0.m3u8
            """;

        var source = XtreamCatchupChannel.ParseHlsMasterMediaSource("item123", "http://127.0.0.1:5050/timeshift/u/p/60/2026-11-19:22-00/4044.m3u8", master);

        Assert.Equal("hls", source.Container);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.True(source.IsInfiniteStream);
        Assert.Null(source.RunTimeTicks);
        Assert.False(source.SupportsProbing);
        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.Equal(3, source.MediaStreams.Count);
        Assert.Equal("hevc", source.MediaStreams[0].Codec);
        Assert.Equal(1280, source.MediaStreams[0].Width);
        Assert.Equal(720, source.MediaStreams[0].Height);
        Assert.Equal("aac", source.MediaStreams[1].Codec);
        Assert.Equal("slk", source.MediaStreams[1].Language);
        Assert.True(source.MediaStreams[1].IsDefault);
        Assert.Equal("mul", source.MediaStreams[2].Language);
        Assert.False(source.MediaStreams[2].IsDefault);
    }
}
