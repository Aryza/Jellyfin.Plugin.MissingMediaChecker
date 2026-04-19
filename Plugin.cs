using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MissingMediaChecker.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MissingMediaChecker;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance    = this;
        ResultsPath = Path.Combine(applicationPaths.DataPath, "missingmediachecker_results.json");
    }

    public static Plugin? Instance    { get; private set; }
    public static string   ResultsPath { get; private set; } = string.Empty;

    public override string Name        => "MissingMediaChecker";
    public override Guid   Id          => Guid.Parse("9b8e1c4a-2d3f-4e5b-a6c7-d8e9f0a1b2c3");
    public override string Description => "Checks for missing episodes, seasons, and collection movies using TMDB.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        // Do NOT set DisplayName, MenuSection, or MenuIcon — they cause a Jellyfin load crash.
        new PluginPageInfo
        {
            Name                 = Name,
            EnableInMainMenu     = true,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    };
}
