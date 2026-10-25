// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #108. The provider clock is the one part of catch-up that fails silently: get it wrong
// and the user gets a healthy stream of the wrong programme, with nothing in any log.

using System;
using System.Globalization;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class CatchupUrlBuilderTests
{
    private static DateTimeOffset Utc(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [Fact]
    public void TheTemplateIsByteIdenticalToWhatTheM3UAlreadyShipped()
    {
        // Pins the refactor. External IPTV clients parse this string; a change here is a change to
        // every existing user's catch-up, and it must not happen as a side effect.
        CatchupUrlBuilder.BuildTemplate("http://host:8080", "user", "pass", 4711)
            .Should().Be("http://host:8080/timeshift/user/pass/{duration}/{start}/4711.ts");
    }

    [Fact]
    public void ThePlaybackUrlHasNothingLeftToSubstitute()
    {
        var url = CatchupUrlBuilder.BuildPlaybackUrl(
            "http://host:8080", "user", "pass", 4711, Utc("2026-07-14T20:30:00Z"), 60, TimeSpan.Zero);

        url.Should().NotContain("{");
        url.Should().Be("http://host:8080/timeshift/user/pass/60/2026-07-14:20-30/4711.ts");
    }

    [Fact]
    public void ThePlaybackStartIsExpressedInTheProvidersClock()
    {
        // The whole point: 20:30 UTC is 22:30 on a provider running UTC+2, and that is the time
        // the provider will look up in its own archive.
        CatchupUrlBuilder.BuildPlaybackUrl(
                "http://host:8080", "user", "pass", 1, Utc("2026-07-14T20:30:00Z"), 30, TimeSpan.FromHours(2))
            .Should().Contain("/2026-07-14:22-30/");
    }

    [Fact]
    public void ANegativeProviderOffsetCanCrossBackOverMidnight()
    {
        CatchupUrlBuilder.BuildPlaybackUrl(
                "http://host:8080", "user", "pass", 1, Utc("2026-07-15T01:00:00Z"), 30, TimeSpan.FromHours(-4))
            .Should().Contain("/2026-07-14:21-00/");
    }

    [Fact]
    public void ATrailingSlashOnTheBaseUrlDoesNotDoubleUp()
    {
        CatchupUrlBuilder.BuildPlaybackUrl(
                "http://host:8080/", "user", "pass", 1, Utc("2026-07-14T20:00:00Z"), 30, TimeSpan.Zero)
            .Should().StartWith("http://host:8080/timeshift/");
    }

    [Fact]
    public void ADurationOfZeroBecomesOneMinute()
    {
        // A zero-length timeshift request returns nothing; one minute at least fails visibly.
        CatchupUrlBuilder.BuildPlaybackUrl(
                "http://host:8080", "user", "pass", 1, Utc("2026-07-14T20:00:00Z"), 0, TimeSpan.Zero)
            .Should().Contain("/1/2026-07-14:20-00/");
    }

    [Fact]
    public void DashUsesTheSameWindowInTheTemplateAndPlaybackUrl()
    {
        CatchupUrlBuilder.BuildTemplate("http://host", "user", "pass", 4099, "mpd")
            .Should().Be("http://host/timeshift/user/pass/{duration}/{start}/4099.mpd");
        CatchupUrlBuilder.BuildPlaybackUrl(
                "http://host", "user", "pass", 4099, Utc("2026-10-24T05:20:00Z"), 40, TimeSpan.Zero, "mpd")
            .Should().Be("http://host/timeshift/user/pass/40/2026-10-24:05-20/4099.mpd");
    }
}

public class CatchupClockTests
{
    private static DateTimeOffset Utc(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [Theory]
    [InlineData("2026-07-14 22:00:00", 2)]
    [InlineData("2026-07-14 20:00:00", 0)]
    [InlineData("2026-07-14 16:00:00", -4)]
    public void TheOffsetIsTheDifferenceBetweenTheTwoClocksTheProviderReports(string localRaw, int expectedHours)
    {
        var utc = new DateTime(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);
        CatchupClock.TryDeriveOffset(utc, localRaw, out var offset).Should().BeTrue();
        offset.Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Fact]
    public void AHalfHourZoneIsPreserved()
    {
        var utc = new DateTime(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);
        CatchupClock.TryDeriveOffset(utc, "2026-07-15 01:30:00", out var offset).Should().BeTrue();
        offset.Should().Be(TimeSpan.FromMinutes(330));
    }

    [Fact]
    public void SecondsOfLatencyBetweenTheTwoFieldsAreRoundedAway()
    {
        // The two values are rendered a moment apart by the panel, so a raw subtraction is never
        // exactly a whole offset.
        var utc = new DateTime(2026, 7, 14, 20, 0, 3, DateTimeKind.Utc);
        CatchupClock.TryDeriveOffset(utc, "2026-07-14 21:59:58", out var offset).Should().BeTrue();
        offset.Should().Be(TimeSpan.FromHours(2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a time")]
    public void AnUnusableLocalTimeIsRefusedRatherThanGuessed(string? raw)
    {
        var utc = new DateTime(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);
        CatchupClock.TryDeriveOffset(utc, raw, out _).Should().BeFalse();
    }

    [Fact]
    public void AnImpossibleOffsetIsRefused()
    {
        // A panel reporting a date years off is broken, not in a strange timezone.
        var utc = new DateTime(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);
        CatchupClock.TryDeriveOffset(utc, "2028-01-01 00:00:00", out _).Should().BeFalse();
    }

    [Fact]
    public void TheMeasuredOffsetWinsOverTheReportedTimezoneName()
    {
        var utc = new DateTime(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);
        var clock = CatchupClock.Resolve(utc, "2026-07-14 22:00:00", "America/New_York", Utc("2026-07-14T20:00:00Z"), 0);

        clock.Offset.Should().Be(TimeSpan.FromHours(2));
        clock.Source.Should().Be("reported by provider");
    }

    [Fact]
    public void TheTimezoneNameIsUsedWhenThereIsNothingToMeasure()
    {
        var clock = CatchupClock.Resolve(default, null, "Europe/Amsterdam", Utc("2026-07-14T20:00:00Z"), 0);

        clock.Offset.Should().Be(TimeSpan.FromHours(2));   // CEST in July
        clock.Source.Should().Be("provider timezone name");
    }

    [Fact]
    public void TheTimezoneNameIsEvaluatedAtTheProgrammesOwnInstant()
    {
        // A programme from January must not be shifted by July's summer-time offset.
        var winter = CatchupClock.Resolve(default, null, "Europe/Amsterdam", Utc("2026-01-14T20:00:00Z"), 0);
        winter.Offset.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void AnUnknownTimezoneNameFallsBackToUtcRatherThanThrowing()
    {
        var clock = CatchupClock.Resolve(default, null, "Mars/Olympus_Mons", Utc("2026-07-14T20:00:00Z"), 0);

        clock.Offset.Should().Be(TimeSpan.Zero);
        clock.Source.Should().Be("assumed UTC");
    }

    [Fact]
    public void WithNothingToGoOnItAssumesUtcAndSaysSo()
    {
        var clock = CatchupClock.Resolve(default, null, null, Utc("2026-07-14T20:00:00Z"), 0);

        clock.Offset.Should().Be(TimeSpan.Zero);
        clock.Source.Should().Be("assumed UTC");
    }

    [Fact]
    public void TheManualCorrectionIsAppliedOnTopOfWhicheverSourceWon()
    {
        var utc = new DateTime(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);
        CatchupClock.Resolve(utc, "2026-07-14 22:00:00", null, Utc("2026-07-14T20:00:00Z"), -60)
            .Offset.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void TheManualCorrectionIsClampedToHalfADay()
    {
        CatchupClock.Resolve(default, null, null, Utc("2026-07-14T20:00:00Z"), 9999)
            .Offset.Should().Be(TimeSpan.FromMinutes(720));
    }
}
