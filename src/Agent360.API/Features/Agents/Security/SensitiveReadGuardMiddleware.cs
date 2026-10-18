using Agent360.API.Features.Agents.SubAgents;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Security;

/// <summary>
/// When Agent360:Security:RequireAuthForSensitiveReads is on, reads of workspace files/trees, chat lists and chat
/// histories need a valid Logto access token (JwtBearer, audience = API resource) or the operator token.
/// Fleet listing, status, health, models and capability lists stay public.
/// </summary>
public sealed class SensitiveReadGuardMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http, IOptionsMonitor<Agent360SecurityOptions> security, IHostEnvironment env)
    {
        var opt = security.CurrentValue;
        if (!opt.RequireAuthForSensitiveReads || !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)))
            return next(http);
        var rel = OperatorWriteGuardMiddleware.Relative(http.Request.Path.Value);
        if (rel is null || !IsSensitive(rel)) return next(http);
        if (http.User.Identity?.IsAuthenticated == true || OperatorAuthFilter.IsAuthorized(http, opt, env)) return next(http);
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return http.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            title = "Unauthorized",
            status = 401,
            detail = "Sign in to read workspace files and chat history.",
        });
    }

    internal static bool IsSensitive(string rel)
    {
        var p = rel.ToLowerInvariant();
        if (!p.StartsWith("agents/") && !p.StartsWith("chats/") && !p.StartsWith("shared/")) return false;
        if (p.StartsWith("shared/")) return false; // explicit share links are public by design
        var parts = p.Split('/');
        // agents/workspace/..., agents/chats/{id}, agents/{id}/chats[/...], agents/{id}/workspace/(tree|file-content|...)
        if (parts.Length >= 2 && parts[0] == "agents" && (parts[1] == "workspace" || parts[1] == "chats" || parts[1] == "sessions")) return true;
        if (parts.Length >= 3 && parts[0] == "agents")
        {
            var sub = parts[2];
            if (sub == "chats") return true;
            if (sub == "workspace" && parts.Length >= 4 && parts[3] is "tree" or "file-content" or "code-files" or "system-prompt-files") return true;
            if (sub == "memory") return true;
        }
        return parts[0] == "chats";
    }
}
