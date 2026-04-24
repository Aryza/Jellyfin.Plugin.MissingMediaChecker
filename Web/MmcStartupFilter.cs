using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.MissingMediaChecker.Web;

/// <summary>
/// Inserts <see cref="ScriptInjectionMiddleware"/> at the very front of the
/// ASP.NET pipeline so it sees the raw GET for index.html before Jellyfin's
/// static-file handler ships the un-patched bytes.
///
/// Registered via <see cref="ServiceRegistrator"/> as an <c>IStartupFilter</c>
/// — Jellyfin invokes IStartupFilters during host build, which is the only
/// supported plugin-side hook for adding middleware.
/// </summary>
public sealed class MmcStartupFilter : IStartupFilter
{
    public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<ScriptInjectionMiddleware>();
            next(app);
        };
    }
}
