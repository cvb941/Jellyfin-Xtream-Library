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

using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Strips Jellyfin's auto-generated folder collage (<see cref="ImageType.Primary"/>) from
/// catch-up day folders (<c>YYYY-MM-DD (...)</c>) so clients render a clean text-only day card
/// while keeping channel logos and programme thumbnails intact.
/// </summary>
public class CatchupDayImageFilter : IAsyncResultFilter
{
    private static readonly Regex _dayFolderName = new(@"^\d{4}-\d{2}-\d{2}\s*\(", RegexOptions.Compiled);

    /// <inheritdoc />
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: QueryResult<BaseItemDto> result }
            && result.Items is not null)
        {
            StripDayFolderPrimaryImages(result);
        }

        return next();
    }

    internal static void StripDayFolderPrimaryImages(QueryResult<BaseItemDto> result)
    {
        foreach (var item in result.Items)
        {
            if (item.IsFolder == true
                && item.ImageTags is not null
                && item.ImageTags.ContainsKey(ImageType.Primary)
                && !string.IsNullOrEmpty(item.Name)
                && _dayFolderName.IsMatch(item.Name))
            {
                item.ImageTags.Remove(ImageType.Primary);
                item.PrimaryImageAspectRatio = null;
            }
        }
    }
}
