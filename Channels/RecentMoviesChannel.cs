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
/// Recently-released movies in the user's library, sorted by release date
/// descending. Reads <see cref="Configuration.PluginConfiguration.RecentMoviesWindowDays"/>.
/// </summary>
public sealed class RecentMoviesChannel : MmcChannelBase
{
    private readonly ILogger<RecentMoviesChannel> _logger;

    public RecentMoviesChannel(ILibraryManager library, ILogger<RecentMoviesChannel> logger)
        : base(library)
    {
        _logger = logger;
    }

    public override string Name        => Plugin.Instance?.Configuration.RecentMoviesChannelName ?? "Recently released";
    public override string Description => "Movies in your library released within the configured window.";

    protected override ChannelMediaContentType ContentType => ChannelMediaContentType.Movie;

    public override bool IsEnabledFor(string userId)
        => Plugin.Instance?.Configuration.EnableRecentMoviesChannel ?? true;

    public override Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var days    = cfg?.RecentMoviesWindowDays ?? 30;
        var maxCap  = cfg?.ChannelMaxItems ?? 50;

        var items = LibraryBridge.RecentMovies(Library, days, maxCap)
            .Select(m => new ChannelItemInfo
            {
                Id              = LibraryId(m.Id),
                Name            = m.Name,
                Overview        = m.Overview,
                Type            = ChannelItemType.Media,
                ContentType     = ChannelMediaContentType.Movie,
                MediaType       = ChannelMediaType.Video,
                PremiereDate    = m.PremiereDate,
                CommunityRating = m.CommunityRating,
                ProductionYear  = m.ProductionYear
            }).ToList();

        return Task.FromResult(new ChannelItemResult
        {
            Items            = items,
            TotalRecordCount = items.Count
        });
    }
}
