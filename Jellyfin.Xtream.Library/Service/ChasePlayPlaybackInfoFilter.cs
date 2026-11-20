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
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Xtream.Library.Api;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Rewrites PlaybackInfo responses for in-progress catch-up HLS chase-play sources so clients
/// stream the provider's HLS EVENT manifest directly via <see cref="ChasePlayController"/>
/// instead of spawning an FFmpeg remux session.
/// </summary>
public class ChasePlayPlaybackInfoFilter : IAsyncResultFilter
{
    /// <inheritdoc />
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: PlaybackInfoResponse response }
            && response.MediaSources is not null)
        {
            foreach (var source in response.MediaSources)
            {
                if (!string.Equals(source.Container, "hls", StringComparison.OrdinalIgnoreCase)
                    || !source.IsInfiniteStream
                    || string.IsNullOrWhiteSpace(source.Id)
                    || string.IsNullOrWhiteSpace(source.Path)
                    || !source.Path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ChasePlayController.Register(source.Id, source.Path);
                source.SupportsDirectPlay = false;
                source.SupportsDirectStream = false;
                source.SupportsTranscoding = true;
                source.TranscodingSubProtocol = MediaStreamProtocol.hls;
                source.TranscodingContainer = "mp4";
                source.TranscodingUrl = $"/XtreamLibrary/ChasePlay/{source.Id}/master.m3u8";
            }
        }

        return next();
    }
}
