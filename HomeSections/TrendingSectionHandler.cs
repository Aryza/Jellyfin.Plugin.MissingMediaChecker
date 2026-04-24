using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MissingMediaChecker.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.HomeSections;

/// <summary>
/// TMDB weekly trending movies filtered to titles already in the library.
/// Invoked by the Home Screen Sections plugin via reflection — the payload
/// carries the requesting user's ID so we can project BaseItemDtos per user.
/// </summary>
public sealed class TrendingSectionHandler : SectionHandlerBase
{
    private readonly ILogger<TrendingSectionHandler> _logger;

    public TrendingSectionHandler(
        ILibraryManager library, IUserManager userManager, IDtoService dto,
        ILogger<TrendingSectionHandler> logger) : base(library, userManager, dto)
    {
        _logger = logger;
    }

    public QueryResult<BaseItemDto> GetResults(SectionPayload payload)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            return new QueryResult<BaseItemDto>();

        // Guids, not BaseItems, are cached — BaseItem instances aren't safe to
        // hold across requests (Jellyfin may evict/refresh them).
        var ids = SectionContentCache.GetOrLoad<List<Guid>>(
            "trending-section", CacheTtl(), () =>
        {
            try
            {
                using var tmdb = new TmdbService(cfg.TmdbApiKey, _logger);
                var trending = tmdb.GetTrendingMoviesAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (trending is null) return new List<Guid>();

                var libMap = LibraryBridge.IndexMoviesByTmdbId(Library);
                return trending.Results
                    .Select(m => libMap.TryGetValue(m.Id, out var movie) ? movie.Id : (Guid?)null)
                    .Where(g => g.HasValue)
                    .Select(g => g!.Value)
                    .Take(MaxItems())
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MissingMediaChecker: trending section load failed");
                return new List<Guid>();
            }
        }) ?? new List<Guid>();

        var items = ids.Select(id => Library.GetItemById(id)).Where(i => i is not null).ToList()!;
        return BuildResult(items!, payload.UserId);
    }
}
