namespace Agent360.API.Features.Agents.Security;

/// <summary>Baseline response hardening for a JSON API, with SAMEORIGIN framing allowed for inline file/artifact preview endpoints.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var h = http.Response.Headers;
            var path = http.Request.Path.Value ?? "";
            var isInlineArtifactPreview =
                path.Contains("/console/files/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/workspace/file-download", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/workspace/file-preview", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/media/files/", StringComparison.OrdinalIgnoreCase);

            h.XContentTypeOptions = "nosniff";
            if (isInlineArtifactPreview)
            {
                h["X-Frame-Options"] = "SAMEORIGIN";
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
                h["Cross-Origin-Resource-Policy"] = "same-site";
                h.ContentSecurityPolicy = "default-src 'self' https: data: blob: 'unsafe-inline' 'unsafe-eval'; img-src * data: blob:; media-src * data: blob:; font-src * data:; frame-ancestors 'self'";
            }
            else
            {
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Cross-Origin-Resource-Policy"] = "same-site";
                h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }

            h.Remove("Server");
            h.Remove("X-Powered-By");
            // API responses carry agent state: never let shared caches keep them unless an endpoint opted in.
            if (!h.ContainsKey("Cache-Control")) h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next(http);
    }
}
