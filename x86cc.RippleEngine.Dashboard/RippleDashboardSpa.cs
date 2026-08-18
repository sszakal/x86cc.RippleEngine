using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace x86cc.RippleEngine.Dashboard;

/// <summary>
/// Serves the dashboard's Angular bundle, which is embedded in this assembly (the csproj builds
/// <c>spa/</c> into <c>dist/</c> and embeds it), so a consumer gets the UI from a package reference alone —
/// no <c>wwwroot</c> to deploy, nothing to copy into a container image.
/// </summary>
public static class RippleDashboardSpa
{
    // The manifest is generated at build time from the embedded dist/ files; a build without Node produced no
    // files and therefore no manifest, in which case the provider constructor throws and the dashboard degrades
    // to "API only" rather than failing the host at startup.
    private static readonly Lazy<IFileProvider?> Files = new(() =>
    {
        try
        {
            return new ManifestEmbeddedFileProvider(typeof(RippleDashboardSpa).Assembly, "dist");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    });

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Maps the SPA as a single <b>fallback</b> endpoint: it serves an embedded asset when the request path
    /// names one, and <c>index.html</c> otherwise so the Angular router owns deep links. Being a fallback, it
    /// has the lowest possible route precedence — every other endpoint in the app, including
    /// <see cref="DashboardApi.MapRippleDashboard"/>'s <c>/api</c> group, wins over it.
    /// </summary>
    /// <remarks>
    /// It is still a catch-all: an app that maps its own SPA fallback must not enable this one (keep
    /// <c>EnableDashboard</c> off and call <see cref="DashboardApi.MapRippleDashboard"/> alone). The SPA is
    /// built with <c>&lt;base href="/"&gt;</c>, so it must be served from the root.
    /// </remarks>
    public static IEndpointRouteBuilder MapRippleDashboardSpa(this IEndpointRouteBuilder endpoints)
    {
        // "{*path}" — NOT the parameterless MapFallback overload, whose pattern is "{*path:nonfile}". The
        // `nonfile` constraint rejects any path whose last segment looks like a file, so every hashed bundle
        // (chunk-*.js, styles-*.css) failed to match ANY endpoint and fell out of the pipeline as a 404 while
        // the extension-less shell and deep links matched fine — the browser got index.html, then 404'd on
        // every script and stylesheet it referenced. That also made the asset branch below dead code. This
        // overload still sets Order = int.MaxValue, so the fallback keeps its lowest-possible precedence and
        // /api (and every host endpoint) continues to win.
        endpoints.MapFallback("{*path}", async context =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (Files.Value is not { } files)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var path = context.Request.Path.Value?.Trim('/');
            var asset = string.IsNullOrEmpty(path) ? null : files.GetFileInfo(path);

            // Not an asset ⇒ an Angular route: hand back the shell and let the client router resolve it.
            var isShell = asset is not { Exists: true };
            var file = isShell ? files.GetFileInfo("index.html") : asset!;
            if (!file.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = ContentTypes.TryGetContentType(file.Name, out var type)
                ? type
                : "application/octet-stream";
            // Angular hashes asset filenames, so they are safe to cache forever; the shell must not be cached
            // or a deployed build keeps serving the previous bundle's script tags.
            context.Response.Headers.CacheControl = isShell ? "no-cache" : "public, max-age=31536000, immutable";
            context.Response.ContentLength = file.Length;

            if (HttpMethods.IsHead(context.Request.Method))
            {
                return;
            }

            await using var stream = file.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });

        return endpoints;
    }
}
