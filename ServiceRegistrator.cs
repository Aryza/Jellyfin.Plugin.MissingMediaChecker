using Jellyfin.Plugin.MissingMediaChecker.HomeSections;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MissingMediaChecker;

/// <summary>
/// Plugin DI wiring. Registers the Trending home-screen section handler
/// (resolved via <see cref="ActivatorUtilities"/> when the Home Screen
/// Sections plugin invokes us by reflection) and the IHostedService that
/// registers the section with that plugin on startup.
///
/// Home Screen Sections (IAmParadox27) is an optional runtime dependency —
/// if its assembly isn't loaded, <see cref="SectionRegistrar"/> logs and exits.
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddTransient<TrendingSectionHandler>();
        services.AddHostedService<SectionRegistrar>();
    }
}
