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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Api;

/// <summary>
/// Serves HLS EVENT master playlists for in-progress catch-up chase-play directly to HLS clients
/// without invoking FFmpeg.
/// </summary>
[ApiController]
[Route("XtreamLibrary/ChasePlay")]
public class ChasePlayController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, string> _routes = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChasePlayController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChasePlayController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public ChasePlayController(IHttpClientFactory httpClientFactory, ILogger<ChasePlayController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Registers an upstream HLS master playlist URL for a catch-up media source ID.
    /// </summary>
    /// <param name="mediaSourceId">Media source ID.</param>
    /// <param name="upstreamUrl">Upstream master playlist URL.</param>
    internal static void Register(string mediaSourceId, string upstreamUrl)
    {
        if (!string.IsNullOrWhiteSpace(mediaSourceId) && !string.IsNullOrWhiteSpace(upstreamUrl))
        {
            _routes[mediaSourceId] = upstreamUrl;
        }
    }

    /// <summary>
    /// Proxies the upstream HLS EVENT master playlist for the requested in-progress catch-up item.
    /// </summary>
    /// <param name="id">The media source ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HLS master playlist.</returns>
    [HttpGet("{id}/master.m3u8")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMasterPlaylist([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (!_routes.TryGetValue(id, out var upstreamUrl))
        {
            return NotFound();
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(upstreamUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Upstream chase-play master playlist returned {StatusCode} for {Id}", response.StatusCode, id);
                return StatusCode((int)response.StatusCode);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Response.Headers.CacheControl = "no-cache";
            return Content(content, "application/vnd.apple.mpegurl");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to proxy chase-play master playlist for {Id}", id);
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
