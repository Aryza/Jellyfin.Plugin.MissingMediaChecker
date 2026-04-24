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
/// Upcoming (future) episodes of series the user already owns, based on
/// TMDB's <c>next_episode_to_air</c> field. Tiles are non-playable — they act
/// as "coming soon" placeholders and link out to TMDB via HomePageUrl. Only
/// runs against non-ended series to cut unnecessary TMDB traffic.
/// </summary>
public sealed class UpcomingEpisodesChannel : MmcChannelBase
{
    private readonly ILogger<UpcomingEpisodesChannel> _logger;

    public UpcomingEpisodesChannel(ILibraryManager library, ILogger<UpcomingEpisodesChannel> logger)
        : base(library)
    {
        _logger = logger;
    }

    public override string Name        => Plugin.Instance?.Configuration.UpcomingEpisodesChannelName ?? "Upcoming episodes";
    public override string Description => "Future episodes of series in your library, via TMDB.";

    protected override ChannelMediaContentType ContentType => ChannelMediaContentType.Episode;

    public override bool IsEnabledFor(string userId)
        => Plugin.Instance?.Configuration.EnableUpcomingEpisodesChannel ?? true;

    public override async Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            return new ChannelItemResult();

        var items = await ChannelContentCache.GetOrLoadAsync<List<ChannelItemInfo>>(
            "upcoming-episodes", CacheTtl(), async innerCt =>
        {
            using var tmdb = new TmdbService(cfg.TmdbApiKey, _logger);
            var seriesList = LibraryBridge.SeriesWithTmdb(Library).ToList();

            var windowDays = Math.Max(1, cfg.UpcomingEpisodesWindowDays);
            var now        = DateTime.UtcNow.Date;
            var horizon    = now.AddDays(windowDays);

            var buckets = new List<ChannelItemInfo>();

            // Parallelism must stay modest — ILibraryManager is not concurrency-
            // friendly, and TMDB rate limits apply. 8 concurrent calls matches
            // what MissingMediaDetector uses elsewhere.
            using var gate = new System.Threading.SemaphoreSlim(8);
            var tasks = seriesList.Select(async sRow =>
            {
                await gate.WaitAsync(innerCt).ConfigureAwait(false);
                try
                {
                    var details = await tmdb.GetSeriesAsync(sRow.TmdbId.ToString(), innerCt).ConfigureAwait(false);
                    if (details?.NextEpisodeToAir is null) return null;
                    if (!DateTime.TryParse(details.NextEpisodeToAir.AirDate, out var airDate)) return null;
                    if (airDate.Date < now || airDate.Date > horizon) return null;

                    var ep       = details.NextEpisodeToAir;
                    var epLabel  = $"S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}";
                    var title    = string.IsNullOrEmpty(ep.Name)
                                       ? $"{sRow.Series.Name} — {epLabel}"
                                       : $"{sRow.Series.Name} — {epLabel} {ep.Name}";
                    return new ChannelItemInfo
                    {
                        Id           = $"tmdb-ep:{sRow.TmdbId}:{ep.SeasonNumber}:{ep.EpisodeNumber}",
                        Name         = title,
                        Overview     = ep.Overview,
                        // Folder so Jellyfin doesn't attempt playback. Users
                        // still see the tile, overview, and air date.
                        Type         = ChannelItemType.Folder,
                        ImageUrl     = PosterUrl(details.PosterPath),
                        PremiereDate = airDate
                    };
                }
                finally { gate.Release(); }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var r in results) if (r is not null) buckets.Add(r);

            // Sort by airing date ascending so "next up" is first.
            buckets.Sort((a, b) =>
                Nullable.Compare(a.PremiereDate, b.PremiereDate));

            return TakeMax(buckets).ToList();
        }, ct).ConfigureAwait(false);

        return new ChannelItemResult
        {
            Items            = items ?? new List<ChannelItemInfo>(),
            TotalRecordCount = items?.Count ?? 0
        };
    }
}
