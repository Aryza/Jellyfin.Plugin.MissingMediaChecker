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
}
