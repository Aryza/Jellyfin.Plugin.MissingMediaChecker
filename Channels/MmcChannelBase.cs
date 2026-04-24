using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MissingMediaChecker.Channels;

/// <summary>
/// Shared IChannel plumbing so each concrete channel only implements the
/// parts that differ (name, description, item loader). Keeps behaviour
/// consistent across the four MMC channels.
/// </summary>
public abstract class MmcChannelBase : IChannel, IRequiresMediaInfoCallback
{
    protected readonly ILibraryManager Library;

    protected MmcChannelBase(ILibraryManager library)
    {
        Library = library;
    }

    public abstract string Name         { get; }
    public abstract string Description  { get; }
    public virtual  string DataVersion  => "1";
    public virtual  string HomePageUrl  => "https://www.themoviedb.org/";
    public virtual  ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>Must be overridden by each channel to report whether it's enabled in config.</summary>
    public abstract bool IsEnabledFor(string userId);

    /// <summary>Content type (Movie / Episode) this channel surfaces.</summary>
    protected abstract ChannelMediaContentType ContentType { get; }

    public virtual InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = new List<ChannelMediaContentType> { ContentType },
        MediaTypes   = new List<ChannelMediaType>         { ChannelMediaType.Video },
        MaxPageSize  = Plugin.Instance?.Configuration.ChannelMaxItems ?? 50
    };

    public abstract Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken cancellationToken);

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct)
    {
        // No plugin-bundled icon; return an empty response so Jellyfin falls
        // back to its default channel placeholder. (Shipping a bitmap in the
        // assembly would add maintenance cost for marginal visual gain.)
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    // ── IRequiresMediaInfoCallback ───────────────────────────────────────────
    // Called by Jellyfin when a user plays a channel item. We embed the library
    // item GUID in ChannelItemInfo.Id as "lib:<guid>"; this callback parses that
    // back out and returns the real item's MediaSources so playback streams the
    // underlying file exactly as if it were opened from the normal library.

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("lib:", StringComparison.Ordinal))
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());

        var guidStr = id.Substring(4);
        if (!Guid.TryParse(guidStr, out var guid))
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());

        var item = Library.GetItemById(guid);
        if (item is IHasMediaSources mediaSourcesProvider)
        {
            IEnumerable<MediaSourceInfo> sources = mediaSourcesProvider
                .GetMediaSources(enablePathSubstitution: false);
            return Task.FromResult(sources);
        }
        return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>TMDB poster URL at w342 (good balance of size vs. detail for channel tiles).</summary>
    protected static string? PosterUrl(string? path) =>
        string.IsNullOrEmpty(path) ? null : "https://image.tmdb.org/t/p/w342" + path;

    /// <summary>Stable ID for a library-backed channel item. Parsed by the media-info callback.</summary>
    protected static string LibraryId(Guid itemId) => "lib:" + itemId.ToString("N");

    /// <summary>Cap the returned list at the configured per-channel maximum.</summary>
    protected static IEnumerable<T> TakeMax<T>(IEnumerable<T> source)
    {
        var max = Plugin.Instance?.Configuration.ChannelMaxItems ?? 50;
        return source.Take(max);
    }

    protected static TimeSpan CacheTtl()
        => TimeSpan.FromMinutes(Plugin.Instance?.Configuration.ChannelCacheMinutes ?? 30);
}
