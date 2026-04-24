using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MissingMediaChecker.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>TMDB API key (v3 auth). Obtain from https://www.themoviedb.org/settings/api</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    public bool CheckTvSeries        { get; set; } = true;
    public bool CheckMovieCollections { get; set; } = true;

    /// <summary>Include Season 0 / Specials when checking TV series.</summary>
    public bool IncludeSpecials { get; set; } = false;

    /// <summary>Skip episodes whose air date is in the future or missing.</summary>
    public bool SkipUnairedEpisodes { get; set; } = true;

    /// <summary>Skip collection movies whose release date is in the future.</summary>
    public bool SkipFutureMovies { get; set; } = true;

    // ── Feature 16: incremental / smart scan ──────────────────────────────────

    /// <summary>
    /// When enabled, the detector skips the TMDB round-trip for "Ended" series
    /// whose Jellyfin library content hasn't changed since the last scan, and
    /// re-uses the prior report. Cuts a second full scan of a large TV library
    /// to near-zero API calls.
    /// </summary>
    public bool EnableIncrementalScan { get; set; } = true;

    /// <summary>
    /// Even with EnableIncrementalScan, every series is re-checked once per this
    /// interval regardless of fingerprint match (safety-net to pick up TMDB
    /// edits, status flips, etc.). Hours.
    /// </summary>
    public int IncrementalMinAgeHours { get; set; } = 168; // one week

    // ── Feature 19: recently-aired only ───────────────────────────────────────

    /// <summary>
    /// When enabled, only missing episodes/movies whose air/release date falls
    /// within the last <see cref="RecentlyAiredDays"/> days are reported.
    /// Useful for a "what's new that I'm missing" view.
    /// </summary>
    public bool RecentlyAiredOnly { get; set; } = false;

    /// <summary>Window (in days) for the RecentlyAiredOnly filter.</summary>
    public int RecentlyAiredDays { get; set; } = 30;

    // ── Feature 28: Jellyfin notifications ────────────────────────────────────

    /// <summary>
    /// Fire a Jellyfin notification after each successful scan that found at
    /// least one new missing episode or movie.
    /// </summary>
    public bool EnableNotifications { get; set; } = true;

    // ── v1.2: Channels ─────────────────────────────────────────────────────────
    // Four Jellyfin channels surfaced alongside Movies/Shows. Each can be added
    // as a home-screen row via Jellyfin's native home-section customization.

    /// <summary>TMDB trending movies that exist in your library.</summary>
    public bool EnableTrendingChannel { get; set; } = true;
    public string TrendingChannelName { get; set; } = "Trending (in library)";

    /// <summary>Recently released movies present in your library (by TMDB release date).</summary>
    public bool EnableRecentMoviesChannel { get; set; } = true;
    public string RecentMoviesChannelName { get; set; } = "Recently released";
    public int RecentMoviesWindowDays { get; set; } = 30;

    /// <summary>Recently aired episodes present in your library.</summary>
    public bool EnableRecentEpisodesChannel { get; set; } = true;
    public string RecentEpisodesChannelName { get; set; } = "Recently aired";
    public int RecentEpisodesWindowDays { get; set; } = 30;

    /// <summary>Upcoming (future) episodes of series already in your library.</summary>
    public bool EnableUpcomingEpisodesChannel { get; set; } = true;
    public string UpcomingEpisodesChannelName { get; set; } = "Upcoming episodes";
    public int UpcomingEpisodesWindowDays { get; set; } = 60;

    /// <summary>Max items returned per channel (cap; TMDB trending is naturally ~20).</summary>
    public int ChannelMaxItems { get; set; } = 50;

    /// <summary>Per-channel TMDB result cache TTL (minutes). Prevents hammering TMDB on every browse.</summary>
    public int ChannelCacheMinutes { get; set; } = 30;

    // ── v1.2: Home-screen pill ────────────────────────────────────────────────

    /// <summary>
    /// Inject a small pill into Jellyfin Web's top bar linking to the plugin's
    /// report when there are unacknowledged new missing items. Uses
    /// ScriptInjectionMiddleware to patch index.html at request time.
    /// </summary>
    public bool EnableHomePill { get; set; } = true;
}
