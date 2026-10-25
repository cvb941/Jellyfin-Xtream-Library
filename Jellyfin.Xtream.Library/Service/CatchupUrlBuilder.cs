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
using System.Globalization;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Builds Xtream timeshift URLs (GitHub #108).
/// <para>
/// Both shapes come from here: the template handed to external IPTV clients through the M3U, which
/// keeps its <c>{start}</c> and <c>{duration}</c> placeholders, and the resolved URL the plugin
/// plays itself. One definition, so the plugin cannot advertise one convention and use another
/// against the same provider.
/// </para>
/// </summary>
public static class CatchupUrlBuilder
{
    /// <summary>
    /// The start format the Xtream timeshift endpoint expects.
    /// </summary>
    private const string StartFormat = "yyyy'-'MM'-'dd':'HH'-'mm";

    /// <summary>
    /// The timeshift URL with its placeholders intact, for an external client to fill in.
    /// </summary>
    /// <param name="baseUrl">Provider base URL.</param>
    /// <param name="username">Provider username.</param>
    /// <param name="password">Provider password.</param>
    /// <param name="streamId">Xtream live stream id.</param>
    /// <param name="extension">Stream extension.</param>
    /// <returns>The template.</returns>
    public static string BuildTemplate(string baseUrl, string username, string password, int streamId, string extension = "ts")
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Trim(baseUrl)}/timeshift/{username}/{password}/{{duration}}/{{start}}/{streamId}.{extension}");

    /// <summary>
    /// The timeshift URL for one programme, with nothing left to substitute.
    /// </summary>
    /// <param name="baseUrl">Provider base URL.</param>
    /// <param name="username">Provider username.</param>
    /// <param name="password">Provider password.</param>
    /// <param name="streamId">Xtream live stream id.</param>
    /// <param name="startUtc">Programme start.</param>
    /// <param name="durationMinutes">Programme duration in whole minutes.</param>
    /// <param name="providerOffset">The provider clock's offset from UTC.</param>
    /// <param name="extension">Stream extension.</param>
    /// <returns>A playable URL.</returns>
    public static string BuildPlaybackUrl(
        string baseUrl,
        string username,
        string password,
        int streamId,
        DateTimeOffset startUtc,
        int durationMinutes,
        TimeSpan providerOffset,
        string extension = "ts")
    {
        var start = startUtc.ToOffset(providerOffset).ToString(StartFormat, CultureInfo.InvariantCulture);
        var minutes = Math.Max(1, durationMinutes);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Trim(baseUrl)}/timeshift/{username}/{password}/{minutes}/{start}/{streamId}.{extension}");
    }

    // Credentials are interpolated raw, exactly as the live stream URL and the shipped M3U
    // template already do. A password containing '/' or '#' produces a broken URL on every one of
    // those paths, not just this one, so escaping belongs in a change that covers them together
    // and can be tested against a real provider. Doing it here alone would make catch-up disagree
    // with live playback about the same credentials.
    private static string Trim(string? baseUrl)
        => (baseUrl ?? string.Empty).TrimEnd('/');
}
