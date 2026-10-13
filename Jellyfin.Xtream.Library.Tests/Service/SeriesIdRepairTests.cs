// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// GitHub #153: in Dispatcharr Mode, versions before 2.0.12.0 took series episodes from
/// Dispatcharr's REST API and wrote STRMs with ids its /series/ route does not accept. Series now
/// always come from get_series_info, and the STRMs already on disk are rewritten once, which the
/// incremental filter and smart skip would otherwise never do.
/// </summary>
[Collection("PluginSingletonTests")]
public class SeriesIdRepairTests : IDisposable
{
    private const string GoodUrl = "http://provider.test/series/u/p/11.mkv";
    private const string BrokenUrl = "http://provider.test/series/u/p/999.mkv";

    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();

    public SeriesIdRepairTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-seriesids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);

        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Series> { new() { SeriesId = 7, Name = "Repair Show (2024)" } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>>
                {
                    [1] = new List<Episode> { new() { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = "Pilot", ContainerExtension = "mkv" } },
                },
            });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_libraryPath))
            {
                Directory.Delete(_libraryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the suite.
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, false)]
    public void SeriesIdsNeedRepair_OnlyForDispatcharrModeWithAnOldSnapshot(
        bool dispatcharrMode, bool markerSet, bool hasSnapshot, bool expected)
    {
        var provider = new ProviderConfig
        {
            EnableDispatcharrMode = dispatcharrMode,
            DispatcharrApiUser = dispatcharrMode ? "sync" : string.Empty,
        };
        var snapshot = hasSnapshot ? new ContentSnapshot { SeriesIdsFromXtreamApi = markerSet } : null;

        StrmSyncService.SeriesIdsNeedRepair(provider, snapshot).Should().Be(expected);
    }

    [Fact]
    public void SeriesIdsNeedRepair_DispatcharrModeWithoutApiUser_IsNotDispatcharrMode()
    {
        // Same rule as everywhere else: the switch without a login does not use Dispatcharr.
        var provider = new ProviderConfig { EnableDispatcharrMode = true, DispatcharrApiUser = string.Empty };

        StrmSyncService.SeriesIdsNeedRepair(provider, new ContentSnapshot()).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void SeriesIdsFromXtreamApiAfterRun_OnlyAdvancesWhenSeriesWereSynced(
        bool syncedSeries, bool previousMarker, bool expected)
    {
        // A run that skipped series has rewritten nothing, so it must not record the repair as done.
        StrmSyncService.SeriesIdsFromXtreamApiAfterRun(syncedSeries, new ContentSnapshot { SeriesIdsFromXtreamApi = previousMarker })
            .Should().Be(expected);
    }

    [Fact]
    public void SeriesIdsFromXtreamApiAfterRun_FirstRunWithoutSeries_StaysFalse()
    {
        StrmSyncService.SeriesIdsFromXtreamApiAfterRun(false, null).Should().BeFalse();
    }

    [Fact]
    public async Task OldSnapshotInDispatcharrMode_RewritesTheBrokenStrmDespiteSmartSkipAndIncrementalSync()
    {
        await RunSyncAsync(dispatcharrMode: true).ConfigureAwait(true);
        var strm = SingleStrm();
        (await File.ReadAllTextAsync(strm).ConfigureAwait(true)).Should().Contain(GoodUrl);

        // What an earlier version left behind: a wrong id on disk, a snapshot without the marker.
        await File.WriteAllTextAsync(strm, BrokenUrl).ConfigureAwait(true);
        ClearMarkerInLatestSnapshot();

        await RunSyncAsync(dispatcharrMode: true).ConfigureAwait(true);

        (await File.ReadAllTextAsync(strm).ConfigureAwait(true)).Should().Contain(GoodUrl);
        LatestSnapshotText().Should().MatchRegex("\"SeriesIdsFromXtreamApi\"\\s*:\\s*true");
    }

    [Fact]
    public async Task CurrentSnapshot_KeepsSkippingAsBefore()
    {
        // The control for the test above: without the old snapshot nothing re-reads the series,
        // so it is the repair, not an ordinary sync, that rewrites the file.
        await RunSyncAsync(dispatcharrMode: true).ConfigureAwait(true);
        var strm = SingleStrm();
        await File.WriteAllTextAsync(strm, BrokenUrl).ConfigureAwait(true);

        await RunSyncAsync(dispatcharrMode: true).ConfigureAwait(true);

        (await File.ReadAllTextAsync(strm).ConfigureAwait(true)).Should().Be(BrokenUrl);
    }

    private string SingleStrm()
        => Directory.GetFiles(Path.Combine(_libraryPath, "Series"), "*.strm", SearchOption.AllDirectories).Should().ContainSingle().Subject;

    private string LatestSnapshotFile()
        => Directory.GetFiles(_libraryPath, "snapshot_*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Last();

    private string LatestSnapshotText() => File.ReadAllText(LatestSnapshotFile());

    private void ClearMarkerInLatestSnapshot()
    {
        var file = LatestSnapshotFile();
        var text = File.ReadAllText(file);
        var pattern = new Regex("(\"SeriesIdsFromXtreamApi\"\\s*:\\s*)true");
        pattern.IsMatch(text).Should().BeTrue("the snapshot has to carry the marker for this test to mean anything");
        File.WriteAllText(file, pattern.Replace(text, "${1}false"));
    }

    private async Task<SyncResult> RunSyncAsync(bool dispatcharrMode)
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_libraryPath);
        appPaths.Setup(p => p.DataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.CachePath).Returns(_libraryPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_libraryPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_libraryPath);

        var plugin = new Plugin(appPaths.Object, new RealXmlSerializer());
        plugin.Configuration.Providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "test",
                BaseUrl = "http://provider.test",
                Username = "u",
                Password = "p",
                LibraryPath = _libraryPath,
                SyncMovies = false,
                SyncSeries = true,
                EnableIncrementalSync = true,
                SmartSkipExisting = true,
                DownloadArtworkForUnmatched = false,
                SyncParallelism = 1,
                EnableDispatcharrMode = dispatcharrMode,
                DispatcharrApiUser = dispatcharrMode ? "sync" : string.Empty,
            },
        };
        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = false;

        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");

        var service = new StrmSyncService(
            _client.Object,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IMetadataLookupService>().Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new DeltaCalculator(NullLogger<DeltaCalculator>.Instance),
            new LiveTvService(_client.Object, new Mock<IDispatcharrClient>().Object, appPaths.Object, host.Object, NullLogger<LiveTvService>.Instance),
            appPaths.Object,
            NullLogger<StrmSyncService>.Instance);

        return await service.SyncAsync(CancellationToken.None).ConfigureAwait(true);
    }
}
