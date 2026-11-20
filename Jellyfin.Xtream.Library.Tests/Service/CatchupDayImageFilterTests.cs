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

using System.Collections.Generic;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class CatchupDayImageFilterTests
{
    [Fact]
    public void StripDayFolderPrimaryImages_RemovesOnlyDayFolderCollages()
    {
        var dayFolder = new BaseItemDto
        {
            Name = "2026-11-20 (Today, pi)",
            IsFolder = true,
            PrimaryImageAspectRatio = 1.77,
            ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "collage1" },
        };
        var channelFolder = new BaseItemDto
        {
            Name = "Markíza HD",
            IsFolder = true,
            ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "logo1" },
        };
        var programme = new BaseItemDto
        {
            Name = "20.11. 08:40 Utajený šéf I.",
            IsFolder = false,
            ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "thumb1" },
        };

        var result = new QueryResult<BaseItemDto>(new[] { dayFolder, channelFolder, programme });
        CatchupDayImageFilter.StripDayFolderPrimaryImages(result);

        Assert.False(dayFolder.ImageTags.ContainsKey(ImageType.Primary));
        Assert.Null(dayFolder.PrimaryImageAspectRatio);
        Assert.True(channelFolder.ImageTags.ContainsKey(ImageType.Primary));
        Assert.True(programme.ImageTags.ContainsKey(ImageType.Primary));
    }
}
