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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Holds one channel's EPG for as long as someone is browsing it, and the provider's clock offset
/// (GitHub #108).
/// <para>
/// Catch-up fetches EPG one channel at a time, and only when a user opens that channel. The whole
/// browse is shaped around avoiding the alternative: a provider with 5000 channels would need 5000
/// requests to draw a screen whose channels nobody has opened yet. One request per channel opened
/// is the cost, and this cache is what stops paging between days repeating it.
/// </para>
/// </summary>
public sealed class CatchupEpgCache
{
    /// <summary>
    /// Ceiling on remembered channels. A scripted walk of a large provider would otherwise grow
    /// this without bound.
    /// </summary>
    internal const int MaxEntries = 512;

    private static readonly TimeSpan ClockTtl = TimeSpan.FromHours(6);

    private readonly IXtreamClient _client;
    private readonly ILogger<CatchupEpgCache> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _programmes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, (ProviderClock Clock, DateTime FetchedUtc)> _clocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CatchupEpgCache"/> class.
    /// </summary>
    /// <param name="client">The Xtream client.</param>
    /// <param name="logger">Logger.</param>
    public CatchupEpgCache(IXtreamClient client, ILogger<CatchupEpgCache> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Builds the cache key for one channel.
    /// </summary>
    /// <param name="providerIndex">Index into the configured providers.</param>
    /// <param name="streamId">Xtream live stream id.</param>
    /// <returns>The key.</returns>
    internal static string CacheKey(int providerIndex, int streamId)
        => string.Create(CultureInfo.InvariantCulture, $"{providerIndex}:{streamId}");

    /// <summary>
    /// Whether a cached entry has aged out.
    /// <para>
    /// A timestamp in the future means the clock moved, not that the entry is infinitely old, so
    /// it counts as fresh. Same reasoning as the snapshot refresh check.
    /// </para>
    /// </summary>
    /// <param name="fetchedAtUtc">When the entry was stored.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ttlMinutes">Lifetime in minutes.</param>
    /// <returns>True when it should be refetched.</returns>
    internal static bool IsExpired(DateTime fetchedAtUtc, DateTime nowUtc, int ttlMinutes)
    {
        if (fetchedAtUtc > nowUtc)
        {
            return false;
        }

        return nowUtc - fetchedAtUtc > TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
    }

    /// <summary>
    /// The EPG for one channel, fetched if it is not already held.
    /// <para>
    /// Returns the whole window the provider gave, not a slice: the caller decides which day it
    /// wants, so browsing seven days of one channel costs one request rather than seven.
    /// </para>
    /// </summary>
    /// <param name="providerIndex">Index into the configured providers.</param>
    /// <param name="streamId">Xtream live stream id.</param>
    /// <param name="ttlMinutes">How long an entry stays usable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The channel's programmes, empty when the provider had none to give.</returns>
    public async Task<IReadOnlyList<EpgProgram>> GetProgrammesAsync(
        int providerIndex,
        int streamId,
        int ttlMinutes,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(providerIndex, streamId);

        var hasCached = _programmes.TryGetValue(key, out var cached);
        if (hasCached && !IsExpired(cached.FetchedUtc, DateTime.UtcNow, ttlMinutes))
        {
            return cached.Programmes;
        }

        IReadOnlyList<EpgProgram> programmes;
        try
        {
            var connection = Plugin.Instance.GetCreds(providerIndex);
            var listings = await _client.GetSimpleDataTableAsync(connection, streamId, cancellationToken).ConfigureAwait(false);
            programmes = listings?.Listings ?? new List<EpgProgram>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not refresh catch-up guide for provider {Provider} channel {StreamId}",
                providerIndex,
                streamId);
            if (hasCached && cached.Programmes.Count > 0)
            {
                return cached.Programmes;
            }

            throw;
        }

        Evict();
        _programmes[key] = new CacheEntry(programmes, DateTime.UtcNow);
        return programmes;
    }

    /// <summary>
    /// The provider's clock offset, measured once and then held.
    /// </summary>
    /// <param name="providerIndex">Index into the configured providers.</param>
    /// <param name="atUtc">The instant the offset is wanted for.</param>
    /// <param name="manualShiftMinutes">User correction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The offset and where it came from.</returns>
    public async Task<ProviderClock> GetClockAsync(
        int providerIndex,
        DateTimeOffset atUtc,
        int manualShiftMinutes,
        CancellationToken cancellationToken)
    {
        if (_clocks.TryGetValue(providerIndex, out var held)
            && DateTime.UtcNow - held.FetchedUtc < ClockTtl)
        {
            return held.Clock;
        }

        ProviderClock clock;
        try
        {
            var connection = Plugin.Instance.GetCreds(providerIndex);
            var info = await _client.GetUserAndServerInfoAsync(connection, cancellationToken).ConfigureAwait(false);
            clock = CatchupClock.Resolve(
                info.ServerInfo.ServerTimeUtc,
                info.ServerInfo.ServerTimeLocalRaw,
                info.ServerInfo.Timezone,
                atUtc,
                manualShiftMinutes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the clock of provider {Provider}; assuming UTC for catch-up", providerIndex);
            clock = CatchupClock.Resolve(default, null, null, atUtc, manualShiftMinutes);
        }

        // Once per provider per six hours, and it is the line that turns "catch-up plays the wrong
        // programme" into a five-minute diagnosis.
        _logger.LogInformation(
            "Catch-up: provider {Provider} clock is UTC{Offset} (source: {Source}, manual correction {Shift} min)",
            providerIndex,
            clock.Offset,
            clock.Source,
            manualShiftMinutes);

        _clocks[providerIndex] = (clock, DateTime.UtcNow);
        return clock;
    }

    /// <summary>
    /// Drops everything held, for when the configuration changes underneath.
    /// </summary>
    public void Invalidate()
    {
        _programmes.Clear();
        _clocks.Clear();
    }

    private void Evict()
    {
        if (_programmes.Count < MaxEntries)
        {
            return;
        }

        foreach (var stale in _programmes.OrderBy(e => e.Value.FetchedUtc).Take(_programmes.Count - MaxEntries + 1).ToList())
        {
            _programmes.TryRemove(stale.Key, out _);
        }
    }

    private readonly record struct CacheEntry(IReadOnlyList<EpgProgram> Programmes, DateTime FetchedUtc);
}
