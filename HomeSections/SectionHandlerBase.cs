using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.MissingMediaChecker.HomeSections;

/// <summary>
/// Shared helpers for the three Home Screen Sections handlers. Centralises
/// <see cref="BaseItem"/> → <see cref="BaseItemDto"/> conversion and the DTO
/// options that make cards render with primary/thumb/backdrop images.
/// </summary>
public abstract class SectionHandlerBase
{
    protected readonly ILibraryManager Library;
    protected readonly IUserManager UserManager;
    protected readonly IDtoService Dto;

    protected SectionHandlerBase(ILibraryManager library, IUserManager userManager, IDtoService dto)
    {
        Library     = library;
        UserManager = userManager;
        Dto         = dto;
    }

    protected QueryResult<BaseItemDto> BuildResult(IReadOnlyList<BaseItem> items, System.Guid userId)
    {
        var user = UserManager.GetUserById(userId);
        var options = new DtoOptions
        {
            Fields = new List<ItemFields>
            {
                ItemFields.PrimaryImageAspectRatio,
                ItemFields.DateCreated,
                ItemFields.Path,
                ItemFields.Overview
            },
            ImageTypeLimit = 1,
            ImageTypes     = new List<ImageType> { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop }
        };
        var dtos = Dto.GetBaseItemDtos(items, options, user);
        return new QueryResult<BaseItemDto>(null, items.Count, dtos);
    }

    protected static int MaxItems()
        => System.Math.Max(1, Plugin.Instance?.Configuration.ChannelMaxItems ?? 50);

    protected static System.TimeSpan CacheTtl()
        => System.TimeSpan.FromMinutes(System.Math.Max(1, Plugin.Instance?.Configuration.ChannelCacheMinutes ?? 30));
}
