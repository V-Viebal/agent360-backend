namespace Agent360.API.Features.Agents.Security;

/// <summary>Baseline response hardening for a JSON API (no HTML is served here).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var h = http.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cross-Origin-Resource-Policy"] = "same-site";
            h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            h.Remove("Server");
            h.Remove("X-Powered-By");
            // API responses carry agent state: never let shared caches keep them unless an endpoint opted in.
            if (!h.ContainsKey("Cache-Control")) h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next(http);
    }
}
