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
/// Recently-aired episodes present in the library. Surfaces episodes whose
/// PremiereDate (air date) falls within
/// <see cref="Configuration.PluginConfiguration.RecentEpisodesWindowDays"/>.
/// </summary>
public sealed class RecentEpisodesChannel : MmcChannelBase
{
    private readonly ILogger<RecentEpisodesChannel> _logger;

    public RecentEpisodesChannel(ILibraryManager library, ILogger<RecentEpisodesChannel> logger)
        : base(library)
    {
        _logger = logger;
    }

    public override string Name        => Plugin.Instance?.Configuration.RecentEpisodesChannelName ?? "Recently aired";
    public override string Description => "Episodes in your library that aired recently.";

    protected override ChannelMediaContentType ContentType => ChannelMediaContentType.Episode;

    public override bool IsEnabledFor(string userId)
        => Plugin.Instance?.Configuration.EnableRecentEpisodesChannel ?? true;

    public override Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken ct)
    {
        var cfg    = Plugin.Instance?.Configuration;
        var days   = cfg?.RecentEpisodesWindowDays ?? 30;
        var maxCap = cfg?.ChannelMaxItems ?? 50;

        var items = LibraryBridge.RecentEpisodes(Library, days, maxCap)
            .Select(ep =>
            {
                var seriesName = ep.Series?.Name ?? string.Empty;
                var seasonNo   = ep.ParentIndexNumber ?? 0;
                var epNo       = ep.IndexNumber ?? 0;
                var name       = $"{seriesName} — S{seasonNo:D2}E{epNo:D2} {ep.Name}";
                return new ChannelItemInfo
                {
                    Id              = LibraryId(ep.Id),
                    Name            = name,
                    Overview        = ep.Overview,
                    Type            = ChannelItemType.Media,
                    ContentType     = ChannelMediaContentType.Episode,
                    MediaType       = ChannelMediaType.Video,
                    PremiereDate    = ep.PremiereDate,
                    CommunityRating = ep.CommunityRating,
                    ProductionYear  = ep.ProductionYear
                };
            }).ToList();

        return Task.FromResult(new ChannelItemResult
        {
            Items            = items,
            TotalRecordCount = items.Count
        });
    }
}
