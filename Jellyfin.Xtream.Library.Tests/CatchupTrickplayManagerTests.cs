using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests;

public sealed class CatchupTrickplayManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"xtream-tp-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static PluginConfiguration CreateEnabledConfig() => new()
    {
        BaseUrl = "http://provider.test",
        Username = "u",
        Password = "p",
        EnableLiveTv = true,
        EnableCatchup = true,
        ShowCatchupInJellyfin = true,
        EnableCatchupTrickplay = true,
        LiveTvOutputFormat = "mpd",
    };

    private static Video CreateCatchupVideo(Guid id, long startUnix = 1795000000, long stopUnix = 1795003600) => new()
    {
        Id = id,
        ExternalId = CatchupItemId.ForProgramme(0, 4044, startUnix, stopUnix),
    };

    [Fact]
    public async Task GetTrickplayManifest_ReturnsVirtualManifest_WhenEnabledForCatchupProgramme()
    {
        var inner = new Mock<ITrickplayManager>(MockBehavior.Strict);
        var lib = new Mock<ILibraryManager>();
        var encoder = new Mock<IMediaEncoder>();
        var epgCache = new CatchupEpgCache(new Mock<IXtreamClient>().Object, NullLogger<CatchupEpgCache>.Instance);
        var itemId = Guid.NewGuid();
        var video = CreateCatchupVideo(itemId);

        using var sut = new CatchupTrickplayManager(
            inner.Object, lib.Object, encoder.Object, _tempDir, epgCache,
            NullLogger<CatchupTrickplayManager>.Instance, CreateEnabledConfig, null);

        var manifest = await sut.GetTrickplayManifest(video);

        var msId = itemId.ToString("N", CultureInfo.InvariantCulture);
        Assert.True(manifest.ContainsKey(msId));
        Assert.True(manifest[msId].ContainsKey(320));
        var info = manifest[msId][320];
        Assert.Equal(320, info.Width);
        Assert.Equal(180, info.Height);
        Assert.Equal(5, info.TileWidth);
        Assert.Equal(2, info.TileHeight);
        Assert.Equal(10000, info.Interval);
        Assert.Equal(360, info.ThumbnailCount);
    }

    [Fact]
    public async Task GetTrickplayManifest_DelegatesToInner_WhenDisabled()
    {
        var expected = new Dictionary<string, Dictionary<int, TrickplayInfo>>();
        var inner = new Mock<ITrickplayManager>();
        inner.Setup(m => m.GetTrickplayManifest(It.IsAny<BaseItem>())).ReturnsAsync(expected);
        var lib = new Mock<ILibraryManager>();
        var encoder = new Mock<IMediaEncoder>();
        var epgCache = new CatchupEpgCache(new Mock<IXtreamClient>().Object, NullLogger<CatchupEpgCache>.Instance);
        var config = CreateEnabledConfig();
        config.EnableCatchupTrickplay = false;

        using var sut = new CatchupTrickplayManager(
            inner.Object, lib.Object, encoder.Object, _tempDir, epgCache,
            NullLogger<CatchupTrickplayManager>.Instance, () => config, null);

        var result = await sut.GetTrickplayManifest(CreateCatchupVideo(Guid.NewGuid()));

        Assert.Same(expected, result);
        inner.Verify(m => m.GetTrickplayManifest(It.IsAny<BaseItem>()), Times.Once);
    }

    [Fact]
    public async Task GetTrickplayTilePathAsync_ReturnsCachedFile_WithoutCallingGenerator()
    {
        var inner = new Mock<ITrickplayManager>(MockBehavior.Strict);
        var lib = new Mock<ILibraryManager>();
        var encoder = new Mock<IMediaEncoder>();
        var epgCache = new CatchupEpgCache(new Mock<IXtreamClient>().Object, NullLogger<CatchupEpgCache>.Instance);
        var itemId = Guid.NewGuid();
        var video = CreateCatchupVideo(itemId, 1795000000, 1795000100);

        var cachedPath = Path.Combine(_tempDir, itemId.ToString("N", CultureInfo.InvariantCulture), "320", "0.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        await File.WriteAllBytesAsync(cachedPath, new byte[] { 0xFF, 0xD8, 0xFF });

        var calls = 0;
        using var sut = new CatchupTrickplayManager(
            inner.Object, lib.Object, encoder.Object, _tempDir, epgCache,
            NullLogger<CatchupTrickplayManager>.Instance, CreateEnabledConfig,
            (_, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(true);
            });

        var result = await sut.GetTrickplayTilePathAsync(video, 320, 0, false);

        Assert.Equal(cachedPath, result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GetTrickplayTilePathAsync_ReturnsEmpty_ForOutOfRangeTileIndex()
    {
        var inner = new Mock<ITrickplayManager>(MockBehavior.Strict);
        var lib = new Mock<ILibraryManager>();
        var encoder = new Mock<IMediaEncoder>();
        var epgCache = new CatchupEpgCache(new Mock<IXtreamClient>().Object, NullLogger<CatchupEpgCache>.Instance);
        var video = CreateCatchupVideo(Guid.NewGuid(), 1795000000, 1795000600); // 600s = 60 thumbs = 6 tiles (0..5)

        using var sut = new CatchupTrickplayManager(
            inner.Object, lib.Object, encoder.Object, _tempDir, epgCache,
            NullLogger<CatchupTrickplayManager>.Instance, CreateEnabledConfig, null);

        var result = await sut.GetTrickplayTilePathAsync(video, 320, 6, false);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildHlsPlaylist_ContainsValidTileEntries()
    {
        var m3u8 = CatchupTrickplayManager.BuildHlsPlaylist(250, "tok123");

        Assert.Contains("#EXTM3U", m3u8, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-IMAGES-ONLY", m3u8, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-TILES:RESOLUTION=320x180,LAYOUT=5x2,DURATION=10.000", m3u8, StringComparison.Ordinal);
        Assert.Contains("0.jpg?api_key=tok123", m3u8, StringComparison.Ordinal);
        Assert.Contains("2.jpg?api_key=tok123", m3u8, StringComparison.Ordinal);
        Assert.DoesNotContain("3.jpg", m3u8, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PruneExpiredCache_RemovesOldTilesAndKeepsRecentTiles()
    {
        var oldFile = Path.Combine(_tempDir, "olditem", "320", "0.jpg");
        var freshFile = Path.Combine(_tempDir, "newitem", "320", "0.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(freshFile)!);
        await File.WriteAllBytesAsync(oldFile, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(freshFile, new byte[] { 4, 5, 6 });

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(oldFile, now.AddDays(-10));
        File.SetLastWriteTimeUtc(freshFile, now.AddDays(-2));

        var deleted = CatchupTrickplayManager.PruneExpiredCache(_tempDir, now, retentionDays: 8);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "olditem")));
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public async Task GetTrickplayTilePathAsync_PrefetchesNextTileInBackground()
    {
        var inner = new Mock<ITrickplayManager>(MockBehavior.Strict);
        var lib = new Mock<ILibraryManager>();
        var encoder = new Mock<IMediaEncoder>();
        var xtream = new Mock<IXtreamClient>();
        var epgCache = new CatchupEpgCache(xtream.Object, NullLogger<CatchupEpgCache>.Instance);
        var itemId = Guid.NewGuid();
        var video = CreateCatchupVideo(itemId, 1795000000, 1795000300); // 300s = 3 tiles (0..2)

        var cached0 = Path.Combine(_tempDir, itemId.ToString("N", CultureInfo.InvariantCulture), "320", "0.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(cached0)!);
        await File.WriteAllBytesAsync(cached0, new byte[] { 0xFF, 0xD8, 0xFF });

        var prefetched = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sut = new CatchupTrickplayManager(
            inner.Object, lib.Object, encoder.Object, _tempDir, epgCache,
            NullLogger<CatchupTrickplayManager>.Instance, CreateEnabledConfig,
            async (_, _, startOffset, _, outPath, _) =>
            {
                await File.WriteAllBytesAsync(outPath, new byte[] { 0xFF, 0xD8, 0xFF });
                prefetched.TrySetResult(startOffset);
                return true;
            });

        var result = await sut.GetTrickplayTilePathAsync(video, 320, 0, false);

        Assert.Equal(cached0, result);
        var offset = await prefetched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, offset);
    }
}
