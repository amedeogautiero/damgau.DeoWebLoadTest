using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;

namespace LoadTest.Controller;

/// <summary>
/// Serve il frontend della dashboard da risorse incorporate nell'assembly LoadTest.Controller.
/// </summary>
internal sealed class EmbeddedDashboardMiddleware(RequestDelegate next)
{
    private const string ResourcePrefix = "LoadTest.Controller.wwwroot.";
    private static readonly Assembly ResourceAssembly = typeof(EmbeddedDashboardMiddleware).Assembly;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var requestPath = context.Request.Path.Value;
        var relativePath = string.IsNullOrEmpty(requestPath) || requestPath == "/"
            ? "index.html"
            : requestPath.TrimStart('/');

        // La dashboard espone solo risorse presenti nell'assembly; non viene effettuato accesso al filesystem.
        if (relativePath.Contains("..", StringComparison.Ordinal) ||
            relativePath.Contains('\\') ||
            relativePath.Contains('?') ||
            relativePath.Contains('#'))
        {
            await next(context);
            return;
        }

        var resourceName = ResourcePrefix + relativePath.Replace('/', '.');
        await using var stream = ResourceAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            await next(context);
            return;
        }

        if (!ContentTypes.TryGetContentType(relativePath, out var contentType))
            contentType = "application/octet-stream";

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = stream.Length;
        context.Response.Headers.CacheControl = "no-cache";

        if (HttpMethods.IsGet(context.Request.Method))
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
