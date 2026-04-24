using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MissingMediaChecker.Api;

/// <summary>
/// Serves the small client-side script injected into Jellyfin Web's index.html
/// by <see cref="Web.ScriptInjectionMiddleware"/>. Anonymous because the
/// browser fetches it before any user has authenticated; the script itself
/// silently no-ops when the follow-up authenticated API call returns 401/403.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("MissingMedia")]
public sealed class PillController : ControllerBase
{
    [HttpGet("pill.js")]
    [Produces("application/javascript")]
    public ContentResult GetPill()
    {
        Response.Headers["Cache-Control"] = "no-cache";
        return new ContentResult
        {
            ContentType = "application/javascript; charset=utf-8",
            StatusCode  = 200,
            Content     = PillScript
        };
    }

    // Inline so there's nothing extra to embed/ship. Kept tiny and dependency-
    // free — runs straight after Jellyfin Web boots and probes the summary
    // endpoint. Authenticated users with elevation see a count pill in the
    // top-right corner; everyone else sees nothing.
    private const string PillScript = @"
(function () {
  // Wait for Jellyfin's API client to exist so we can borrow its auth token.
  function waitForClient(cb, tries) {
    tries = tries || 0;
    if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') return cb();
    if (tries > 40) return; // ~10s and give up
    setTimeout(function () { waitForClient(cb, tries + 1); }, 250);
  }

  function authHeaders() {
    try {
      var token = window.ApiClient && window.ApiClient.accessToken && window.ApiClient.accessToken();
      var h = { 'Accept': 'application/json' };
      if (token) h['X-Emby-Token'] = token;
      return h;
    } catch (e) { return {}; }
  }

  function pluginUrl() {
    // Jellyfin's plugin configuration deep link.
    return '#/dashboard/plugins/configurationpage?name=MissingMediaChecker';
  }

  function injectPill(count) {
    if (document.getElementById('mmc-home-pill')) return;
    var a = document.createElement('a');
    a.id = 'mmc-home-pill';
    a.href = pluginUrl();
    a.title = count + ' new missing item' + (count === 1 ? '' : 's') + ' — open Missing Media Checker';
    a.textContent = '⚠ ' + count + ' missing';
    a.style.cssText = [
      'position:fixed', 'top:8px', 'right:12px', 'z-index:9999',
      'background:#c33', 'color:#fff', 'padding:4px 10px', 'border-radius:12px',
      'font:600 12px/1 system-ui,sans-serif', 'text-decoration:none',
      'box-shadow:0 1px 4px rgba(0,0,0,.4)', 'cursor:pointer'
    ].join(';');
    a.addEventListener('mouseenter', function () { a.style.filter = 'brightness(1.15)'; });
    a.addEventListener('mouseleave', function () { a.style.filter = ''; });
    document.body.appendChild(a);
  }

  function check() {
    fetch('/MissingMedia/Results/Summary', { headers: authHeaders(), credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (!j || !j.hasResults) return;
        var n = (j.newMissingEpisodes || 0) + (j.newMissingMovies || 0);
        if (n > 0) injectPill(n);
      })
      .catch(function () { /* network/auth failure: silently skip */ });
  }

  waitForClient(function () {
    // Initial probe + re-probe every 5 minutes so newly-detected items light up
    // without a full page reload.
    check();
    setInterval(check, 5 * 60 * 1000);
  });
})();
";
}
