using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.Web;

/// <summary>
/// Injects a single &lt;script&gt; tag into Jellyfin Web's index.html so the
/// plugin can render a small "missing media" pill in the top-right corner.
///
/// Two paths:
///   1. Direct — resolve index.html under IWebHostEnvironment.WebRootPath and
///      stream the modified bytes.
///   2. Buffered — if the file can't be located (unusual installs), wrap the
///      response stream and rewrite on the fly.
///
/// Cache-Control: no-store on both paths so proxies and CDNs don't pin the
/// pre-injected version after the user toggles the feature off.
/// </summary>
public sealed class ScriptInjectionMiddleware : IMiddleware
{
    private const string Marker    = "/MissingMedia/pill.js";
    private const string ScriptTag = "\n    <script src=\"/MissingMedia/pill.js\" defer></script>";

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    public ScriptInjectionMiddleware(
        IWebHostEnvironment env,
        ILogger<ScriptInjectionMiddleware> logger)
    {
        _env    = env;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!ShouldIntercept(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var indexPath = ResolveIndexHtmlPath();
        if (indexPath is not null)
        {
            await ServeDirectAsync(context, indexPath).ConfigureAwait(false);
            return;
        }

        await ServeBufferedAsync(context, next).ConfigureAwait(false);
    }

    private static bool ShouldIntercept(HttpContext context)
    {
        // Only patch the root Jellyfin web index. Skip API, static assets, and
        // everything else so the middleware adds near-zero overhead.
        if (!HttpMethods.IsGet(context.Request.Method)) return false;
        if (Plugin.Instance?.Configuration.EnableHomePill != true) return false;

        var path = context.Request.Path.Value ?? string.Empty;
        if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/web/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private string? ResolveIndexHtmlPath()
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot) || !Directory.Exists(webRoot)) return null;

        var baseDir = Directory.GetParent(webRoot)?.FullName;
        foreach (var subdir in new[] { "jellyfin-web", "web" })
        {
            if (baseDir is null) break;
            var candidate = Path.Combine(baseDir, subdir, "index.html");
            if (File.Exists(candidate)) return candidate;
        }

        var direct = Path.Combine(webRoot, "index.html");
        return File.Exists(direct) ? direct : null;
    }

    private async Task ServeDirectAsync(HttpContext context, string indexPath)
    {
        try
        {
            var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted).ConfigureAwait(false);

            if (!html.Contains(Marker, StringComparison.Ordinal) &&
                html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
            {
                html = html.Replace("</head>", ScriptTag + "\n</head>", StringComparison.OrdinalIgnoreCase);
            }

            context.Response.ContentType  = "text/html; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MissingMediaChecker: direct index.html injection failed, falling back.");
        }
    }

    private async Task ServeBufferedAsync(HttpContext context, RequestDelegate next)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);

            context.Response.Body = original;
            buffer.Position = 0;

            // Only rewrite text/html bodies; leave binary responses alone.
            var ct = context.Response.ContentType ?? string.Empty;
            if (!ct.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(buffer, leaveOpen: false);
            var html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

            if (!html.Contains(Marker, StringComparison.Ordinal) &&
                html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
            {
                html = html.Replace("</head>", ScriptTag + "\n</head>", StringComparison.OrdinalIgnoreCase);
            }

            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength = null;
            await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }
    }
}
