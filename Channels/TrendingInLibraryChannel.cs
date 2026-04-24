using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingMediaChecker.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.Channels;

/// <summary>
/// Mirrors TMDB's weekly trending-movies list, filtered to titles that already
/// exist in the user's library. Clicking a tile plays the local file via
/// <see cref="MmcChannelBase.GetChannelItemMediaInfo"/>.
/// </summary>
public sealed class TrendingInLibraryChannel : MmcChannelBase
{
    private readonly ILogger<TrendingInLibraryChannel> _logger;

    public TrendingInLibraryChannel(ILibraryManager library, ILogger<TrendingInLibraryChannel> logger)
        : base(library)
    {
        _logger = logger;
    }

    public override string Name        => Plugin.Instance?.Configuration.TrendingChannelName ?? "Trending (in library)";
    public override string Description => "TMDB weekly trending movies that are already in your library.";

    protected override ChannelMediaContentType ContentType => ChannelMediaContentType.Movie;

    public override bool IsEnabledFor(string userId)
        => Plugin.Instance?.Configuration.EnableTrendingChannel ?? true;

    public override async Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            return new ChannelItemResult();

        var items = await ChannelContentCache.GetOrLoadAsync<List<ChannelItemInfo>>(
            "trending-in-library", CacheTtl(), async innerCt =>
        {
            using var tmdb = new TmdbService(cfg.TmdbApiKey, _logger);
            var trending = await tmdb.GetTrendingMoviesAsync(innerCt).ConfigureAwait(false);
            if (trending is null || trending.Results.Count == 0) return new List<ChannelItemInfo>();

            // Index once, intersect in O(n).
            var libMap = LibraryBridge.IndexMoviesByTmdbId(Library);

            var result = new List<ChannelItemInfo>(Math.Min(trending.Results.Count, libMap.Count));
            foreach (var m in trending.Results)
            {
                if (!libMap.TryGetValue(m.Id, out var libMovie)) continue;

                result.Add(new ChannelItemInfo
                {
                    Id           = LibraryId(libMovie.Id),
                    Name         = libMovie.Name,
                    Overview     = m.Overview ?? libMovie.Overview,
                    Type         = ChannelItemType.Media,
                    ContentType  = ChannelMediaContentType.Movie,
                    MediaType    = ChannelMediaType.Video,
                    ImageUrl     = PosterUrl(m.PosterPath),
                    PremiereDate = DateTime.TryParse(m.ReleaseDate, out var d) ? d : null,
                    CommunityRating = m.VoteAverage > 0 ? (float?)m.VoteAverage : null
                });
            }
            return TakeMax(result).ToList();
        }, ct).ConfigureAwait(false);

        return new ChannelItemResult
        {
            Items      = items ?? new List<ChannelItemInfo>(),
            TotalRecordCount = items?.Count ?? 0
        };
    }
}
