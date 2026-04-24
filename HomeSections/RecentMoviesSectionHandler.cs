using System.Linq;
using Jellyfin.Plugin.MissingMediaChecker.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.MissingMediaChecker.HomeSections;

/// <summary>Library movies released within the configured window, newest first.</summary>
public sealed class RecentMoviesSectionHandler : SectionHandlerBase
{
    public RecentMoviesSectionHandler(ILibraryManager library, IUserManager userManager, IDtoService dto)
        : base(library, userManager, dto) { }

    public QueryResult<BaseItemDto> GetResults(SectionPayload payload)
    {
        var cfg   = Plugin.Instance?.Configuration;
        var days  = cfg?.RecentMoviesWindowDays ?? 30;
        var items = LibraryBridge.RecentMovies(Library, days, MaxItems())
            .Cast<BaseItem>()
            .ToList();
        return BuildResult(items, payload.UserId);
    }
}
