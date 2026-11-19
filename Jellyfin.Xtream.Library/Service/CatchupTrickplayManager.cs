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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Decorates Jellyfin's <see cref="ITrickplayManager"/> to serve on-demand scrubbing preview tiles
/// for Xtream catch-up programmes while delegating all other items to the built-in manager.
/// </summary>
public sealed class CatchupTrickplayManager : ITrickplayManager, IDisposable
{
    internal const int DefaultWidth = 320;
    internal const int DefaultHeight = 180;
    internal const int DefaultTileWidth = 5;
    internal const int DefaultTileHeight = 2;
    internal const int DefaultIntervalMs = 10000;
    private const int DefaultBandwidth = 80000;
    private const int MaxConcurrentExtractions = 2;
    private const int ExtractionTimeoutMs = 25000;
    private static readonly long CleanupIntervalTicks = TimeSpan.FromHours(6).Ticks;

    private readonly ITrickplayManager _inner;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly CatchupEpgCache _epgCache;
    private readonly ILogger<CatchupTrickplayManager> _logger;
    private readonly string _cacheRoot;
    private readonly Func<PluginConfiguration> _getConfig;
    private readonly Func<string, string, int, int, string, CancellationToken, Task<bool>> _tileGenerator;
    private readonly SemaphoreSlim _ffmpegGate = new(MaxConcurrentExtractions, MaxConcurrentExtractions);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.Ordinal);
    private long _lastCleanupUtcTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatchupTrickplayManager"/> class.
    /// </summary>
    /// <param name="inner">The built-in Jellyfin trickplay manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="mediaEncoder">The Jellyfin media encoder.</param>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    /// <param name="epgCache">The catch-up EPG cache.</param>
    /// <param name="logger">The logger instance.</param>
    public CatchupTrickplayManager(
        ITrickplayManager inner,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IApplicationPaths applicationPaths,
        CatchupEpgCache epgCache,
        ILogger<CatchupTrickplayManager> logger)
        : this(
            inner,
            libraryManager,
            mediaEncoder,
            Path.Combine(applicationPaths.CachePath, "xtream-trickplay"),
            epgCache,
            logger,
            () => Plugin.Instance.Configuration,
            null)
    {
    }

    internal CatchupTrickplayManager(
        ITrickplayManager inner,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        string cacheRoot,
        CatchupEpgCache epgCache,
        ILogger<CatchupTrickplayManager> logger,
        Func<PluginConfiguration> getConfig,
        Func<string, string, int, int, string, CancellationToken, Task<bool>>? tileGenerator)
    {
        _inner = inner;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _cacheRoot = cacheRoot;
        _epgCache = epgCache;
        _logger = logger;
        _getConfig = getConfig;
        _tileGenerator = tileGenerator ?? ExtractTileWithFfmpegAsync;
    }

    /// <inheritdoc />
    public Task RefreshTrickplayDataAsync(Video video, bool replace, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        => _inner.RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken);

    /// <inheritdoc />
    public TrickplayInfo CreateTiles(IReadOnlyList<string> images, int width, TrickplayOptions options, string outputDir)
        => _inner.CreateTiles(images, width, options, outputDir);

    /// <inheritdoc />
    public Task<Dictionary<int, TrickplayInfo>> GetTrickplayResolutions(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is not null && TryGetCatchupProgramme(item, out _, out var durationSeconds))
        {
            return Task.FromResult(new Dictionary<int, TrickplayInfo>
            {
                [DefaultWidth] = BuildTrickplayInfo(item.Id, durationSeconds),
            });
        }

        return _inner.GetTrickplayResolutions(itemId);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TrickplayInfo>> GetTrickplayItemsAsync(int limit, int offset)
        => _inner.GetTrickplayItemsAsync(limit, offset);

    /// <inheritdoc />
    public Task SaveTrickplayInfo(TrickplayInfo info)
        => _inner.SaveTrickplayInfo(info);

    /// <inheritdoc />
    public Task DeleteTrickplayDataAsync(Guid itemId, CancellationToken cancellationToken)
        => _inner.DeleteTrickplayDataAsync(itemId, cancellationToken);

    /// <inheritdoc />
    public Task<Dictionary<string, Dictionary<int, TrickplayInfo>>> GetTrickplayManifest(BaseItem item)
    {
        if (TryGetCatchupProgramme(item, out _, out var durationSeconds))
        {
            var mediaSourceId = item.Id.ToString("N", CultureInfo.InvariantCulture);
            return Task.FromResult(new Dictionary<string, Dictionary<int, TrickplayInfo>>(StringComparer.Ordinal)
            {
                [mediaSourceId] = new Dictionary<int, TrickplayInfo>
                {
                    [DefaultWidth] = BuildTrickplayInfo(item.Id, durationSeconds),
                },
            });
        }

        return _inner.GetTrickplayManifest(item);
    }

    /// <inheritdoc />
    public async Task<string> GetTrickplayTilePathAsync(BaseItem item, int width, int index, bool saveWithMedia)
    {
        if (width != DefaultWidth || index < 0 || !TryGetCatchupProgramme(item, out var reference, out var durationSeconds))
        {
            return await _inner.GetTrickplayTilePathAsync(item, width, index, saveWithMedia).ConfigureAwait(false);
        }

        var info = BuildTrickplayInfo(item.Id, durationSeconds);
        var thumbsPerTile = DefaultTileWidth * DefaultTileHeight;
        var tileCount = (int)Math.Ceiling((double)info.ThumbnailCount / thumbsPerTile);
        if (index >= tileCount)
        {
            return string.Empty;
        }

        MaybeScheduleCacheCleanup();

        var itemDir = Path.Combine(_cacheRoot, item.Id.ToString("N", CultureInfo.InvariantCulture), width.ToString(CultureInfo.InvariantCulture));
        var tilePath = Path.Combine(itemDir, $"{index.ToString(CultureInfo.InvariantCulture)}.jpg");
        var result = File.Exists(tilePath)
            ? tilePath
            : await GetOrGenerateTileAsync(reference, index, tilePath).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result) && index + 1 < tileCount)
        {
            var nextPath = Path.Combine(itemDir, $"{(index + 1).ToString(CultureInfo.InvariantCulture)}.jpg");
            if (!File.Exists(nextPath) && _ffmpegGate.CurrentCount > 0)
            {
                SchedulePrefetch(reference, index + 1, nextPath);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public string GetTrickplayDirectory(BaseItem item, int tileWidth, int tileHeight, int width, bool saveWithMedia = false)
        => _inner.GetTrickplayDirectory(item, tileWidth, tileHeight, width, saveWithMedia);

    /// <inheritdoc />
    public Task MoveGeneratedTrickplayDataAsync(Video video, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        => _inner.MoveGeneratedTrickplayDataAsync(video, libraryOptions, cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetHlsPlaylist(Guid itemId, int width, string? apiKey)
    {
        if (width == DefaultWidth)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is not null && TryGetCatchupProgramme(item, out _, out var durationSeconds))
            {
                return Task.FromResult<string?>(BuildHlsPlaylist(durationSeconds, apiKey));
            }
        }

        return _inner.GetHlsPlaylist(itemId, width, apiKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ffmpegGate.Dispose();
    }

    internal static TrickplayInfo BuildTrickplayInfo(Guid itemId, int durationSeconds)
    {
        var intervalSec = DefaultIntervalMs / 1000.0;
        var thumbnailCount = Math.Max(1, (int)Math.Ceiling(durationSeconds / intervalSec));
        return new TrickplayInfo
        {
            ItemId = itemId,
            Width = DefaultWidth,
            Height = DefaultHeight,
            TileWidth = DefaultTileWidth,
            TileHeight = DefaultTileHeight,
            ThumbnailCount = thumbnailCount,
            Interval = DefaultIntervalMs,
            Bandwidth = DefaultBandwidth,
        };
    }

    internal static string BuildHlsPlaylist(int durationSeconds, string? apiKey)
    {
        var thumbsPerTile = DefaultTileWidth * DefaultTileHeight;
        var intervalSec = DefaultIntervalMs / 1000;
        var tileDurationSec = thumbsPerTile * intervalSec;
        var thumbnailCount = Math.Max(1, (int)Math.Ceiling((double)durationSeconds / intervalSec));
        var tileCount = (int)Math.Ceiling((double)thumbnailCount / thumbsPerTile);
        var query = string.IsNullOrEmpty(apiKey) ? string.Empty : $"?api_key={Uri.EscapeDataString(apiKey)}";

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:7");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        sb.AppendLine(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{tileDurationSec}");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        sb.AppendLine("#EXT-X-IMAGES-ONLY");
        for (var i = 0; i < tileCount; i++)
        {
            var remainingThumbs = Math.Min(thumbsPerTile, thumbnailCount - (i * thumbsPerTile));
            var extinf = remainingThumbs * intervalSec;
            sb.AppendLine(CultureInfo.InvariantCulture, $"#EXTINF:{extinf}.000,");
            sb.AppendLine(CultureInfo.InvariantCulture, $"#EXT-X-TILES:RESOLUTION={DefaultWidth}x{DefaultHeight},LAYOUT={DefaultTileWidth}x{DefaultTileHeight},DURATION={intervalSec}.000");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i}.jpg{query}");
        }

        sb.AppendLine("#EXT-X-ENDLIST");
        return sb.ToString();
    }

    internal static int PruneExpiredCache(string cacheRoot, DateTime utcNow, int retentionDays)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return 0;
        }

        var cutoff = utcNow.AddDays(-Math.Max(1, retentionDays));
        var deleted = 0;

        foreach (var file in Directory.EnumerateFiles(cacheRoot, "*.jpg", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // Ignore files in use or already deleted.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore permission errors during best-effort cleanup.
            }
        }

        PruneEmptyDirectories(cacheRoot);
        return deleted;
    }

    private static void PruneEmptyDirectories(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                PruneEmptyDirectories(dir);
                if (!Directory.EnumerateFileSystemEntries(dir).GetEnumerator().MoveNext())
                {
                    Directory.Delete(dir, recursive: false);
                }
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private void MaybeScheduleCacheCleanup()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastCleanupUtcTicks);
        if (nowTicks - lastTicks < CleanupIntervalTicks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupUtcTicks, nowTicks, lastTicks) != lastTicks)
        {
            return;
        }

        var retentionDays = Math.Max(1, _getConfig().CatchupDays) + 1;
        _ = Task.Run(() =>
        {
            try
            {
                var deleted = PruneExpiredCache(_cacheRoot, DateTime.UtcNow, retentionDays);
                if (deleted > 0)
                {
                    _logger.LogInformation("Pruned {Count} expired catch-up trickplay tiles from cache", deleted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Best-effort catch-up trickplay cache cleanup failed");
            }
        });
    }

    private void SchedulePrefetch(CatchupItemRef reference, int index, string tilePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GetOrGenerateTileAsync(reference, index, tilePath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background prefetch of catch-up trickplay tile {Index} failed", index);
            }
        });
    }

    private async Task<string> GetOrGenerateTileAsync(CatchupItemRef reference, int index, string tilePath)
    {
        var lazy = _inFlight.GetOrAdd(
            tilePath,
            _ => new Lazy<Task<string>>(() => GenerateTileCachedAsync(reference, index, tilePath)));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(tilePath, lazy));
        }
    }

    private bool TryGetCatchupProgramme(BaseItem item, out CatchupItemRef reference, out int durationSeconds)
    {
        reference = default;
        durationSeconds = 0;

        var config = _getConfig();
        if (!config.EnableCatchupTrickplay || !config.ShowCatchupInJellyfin)
        {
            return false;
        }

        if (item is not Video || string.IsNullOrEmpty(item.ExternalId))
        {
            return false;
        }

        if (!CatchupItemId.TryParse(item.ExternalId, out reference) || reference.Kind != CatchupItemKind.Programme)
        {
            return false;
        }

        var seconds = reference.StopUnix - reference.StartUnix;
        if (seconds <= 0 || seconds > 24 * 3600)
        {
            return false;
        }

        durationSeconds = (int)seconds;
        return true;
    }

    private async Task<string> GenerateTileCachedAsync(CatchupItemRef reference, int index, string tilePath)
    {
        if (File.Exists(tilePath))
        {
            return tilePath;
        }

        await _ffmpegGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(tilePath))
            {
                return tilePath;
            }

            var config = _getConfig();
            var startUtc = DateTimeOffset.FromUnixTimeSeconds(reference.StartUnix);
            using var cts = new CancellationTokenSource(ExtractionTimeoutMs);
            var clock = await _epgCache.GetClockAsync(
                reference.ProviderIndex,
                startUtc,
                config.CatchupTimeShiftMinutes,
                cts.Token).ConfigureAwait(false);

            var (baseUrl, username, password) = LiveTvService.ResolveLiveTvProvider(config, reference.ProviderIndex);
            var minutes = CatchupPlanner.DurationMinutes(reference.StartUnix, reference.StopUnix);
            var isDash = string.Equals(config.LiveTvOutputFormat, "mpd", StringComparison.OrdinalIgnoreCase);
            var extension = isDash ? "mpd" : "ts";
            var url = CatchupUrlBuilder.BuildPlaybackUrl(
                baseUrl, username, password, reference.StreamId, startUtc, minutes, clock.Offset, extension);

            var intervalSec = DefaultIntervalMs / 1000;
            var tileDurationSec = DefaultTileWidth * DefaultTileHeight * intervalSec;
            var startOffsetSec = index * tileDurationSec;

            var dir = Path.GetDirectoryName(tilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmpPath = $"{tilePath}.{Guid.NewGuid():N}.tmp.jpg";
            try
            {
                var ok = await _tileGenerator(url, isDash ? "dash" : string.Empty, startOffsetSec, tileDurationSec, tmpPath, cts.Token).ConfigureAwait(false);
                if (ok && File.Exists(tmpPath) && new FileInfo(tmpPath).Length > 0)
                {
                    File.Move(tmpPath, tilePath, overwrite: true);
                    return tilePath;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Timed out generating catch-up trickplay tile {Index} for {Url}",
                    index,
                    StreamUrlRedactor.Redact(url));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed generating catch-up trickplay tile {Index} for {Url}",
                    index,
                    StreamUrlRedactor.Redact(url));
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch (IOException)
                    {
                        // Best-effort cleanup of temp tile file.
                    }
                }
            }

            return string.Empty;
        }
        finally
        {
            _ffmpegGate.Release();
        }
    }

    private async Task<bool> ExtractTileWithFfmpegAsync(
        string url,
        string inputFormat,
        int startOffsetSec,
        int tileDurationSec,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            return false;
        }

        var intervalSec = DefaultIntervalMs / 1000;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-skip_frame");
        psi.ArgumentList.Add("nokey");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startOffsetSec.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(tileDurationSec.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(inputFormat))
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(inputFormat);
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(string.Format(
            CultureInfo.InvariantCulture,
            "fps=1/{0},scale={1}:{2},tile={3}x{4}:nb_frames=0",
            intervalSec,
            DefaultWidth,
            DefaultHeight,
            DefaultTileWidth,
            DefaultTileHeight));
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("5");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }

            throw;
        }
    }
}
