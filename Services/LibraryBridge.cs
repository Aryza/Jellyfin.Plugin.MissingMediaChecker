using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

/// <summary>
/// Shared helpers for the channel classes. Centralises queries into
/// ILibraryManager so each channel doesn't re-invent BaseItem lookups.
/// </summary>
public static class LibraryBridge
{
    /// <summary>
    /// Map every library movie to its TMDB ID. Used by the trending channel to
    /// filter TMDB trending results down to items the user actually has.
    /// Movies without a TMDB provider ID are omitted.
    /// </summary>
    public static Dictionary<int, Movie> IndexMoviesByTmdbId(ILibraryManager library)
    {
        var map = new Dictionary<int, Movie>();
        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive        = true,
            IsVirtualItem    = false
        });
        foreach (var item in items)
        {
            if (item is not Movie movie) continue;
            if (movie.ProviderIds.TryGetValue("Tmdb", out var tmdbStr) &&
                int.TryParse(tmdbStr, out var tmdbId) && tmdbId > 0)
                map[tmdbId] = movie;
        }
        return map;
    }

    /// <summary>All library movies whose PremiereDate falls within the last <paramref name="days"/>.</summary>
    /// <remarks>Filter + sort done in-memory to stay independent of the SortOrder enum's
    /// namespace shuffles between Jellyfin minor releases.</remarks>
    public static IEnumerable<Movie> RecentMovies(ILibraryManager library, int days, int maxItems)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        return library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive        = true,
            IsVirtualItem    = false
        })
        .OfType<Movie>()
        .Where(m => m.PremiereDate.HasValue && m.PremiereDate.Value >= cutoff)
        .OrderByDescending(m => m.PremiereDate)
        .Take(Math.Max(1, maxItems));
    }

    /// <summary>All library episodes whose PremiereDate (air date) falls within the last <paramref name="days"/>.</summary>
    public static IEnumerable<Episode> RecentEpisodes(ILibraryManager library, int days, int maxItems)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        return library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive        = true,
            IsVirtualItem    = false
        })
        .OfType<Episode>()
        .Where(e => e.PremiereDate.HasValue && e.PremiereDate.Value >= cutoff)
        .OrderByDescending(e => e.PremiereDate)
        .Take(Math.Max(1, maxItems));
    }

    /// <summary>All library series with a TMDB ID resolved. Used by the upcoming-episodes channel.</summary>
    public static IEnumerable<(Series Series, int TmdbId)> SeriesWithTmdb(ILibraryManager library)
    {
        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive        = true
        });
        foreach (var item in items)
        {
            if (item is not Series series) continue;
            if (series.ProviderIds.TryGetValue("Tmdb", out var tmdbStr) &&
                int.TryParse(tmdbStr, out var tmdbId) && tmdbId > 0)
                yield return (series, tmdbId);
        }
    }
}
