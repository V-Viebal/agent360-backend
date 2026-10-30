using Agent360.API.Features.Agents.SubAgents;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Security;

/// <summary>
/// Fail-closed operator check for every mutating Agent360 agent/runtime route that is mapped AllowAnonymous
/// (approvals, compaction, IAM revoke, skill/MCP/tool toggles, schedules, media, sharing, Composio, active model).
/// Enabled by Agent360:Security:ProtectRuntimeProxyWrites (on in staging/production). Chat streaming, chat stop,
/// uploads used by the composer and the turn-telemetry beacon stay open so the chat surface keeps working
/// without an operator key; reads are never affected.
/// </summary>
public sealed class OperatorWriteGuardMiddleware(RequestDelegate next)
{
    private static readonly string[] ProtectedPrefixes =
    [
        "agents/approvals/", "approvals/", "agents/compact-context", "agents/iam/", "libraries/skills/", "mcps/",
        "tools/", "schedules/", "media/", "chats/", "integrations/", "mcp",
    ];

    // Agent-scoped writes handled by dedicated endpoints (the runtime proxy already guards its own prefixes).
    private static readonly string[] ProtectedAgentSuffixes = ["/compact-context", "/active-model", "/media/send"];

    private static readonly string[] OpenWrites =
    [
        "agents/console/chat", "console/chat", "agents/console/upload", "console/upload", "agents/analytics/turn-telemetry",
    ];

    public Task InvokeAsync(HttpContext http, IOptionsMonitor<Agent360SecurityOptions> security, IHostEnvironment env)
    {
        var method = http.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return next(http);
        var opt = security.CurrentValue;
        if (!opt.ProtectRuntimeProxyWrites) return next(http);
        var rel = Relative(http.Request.Path.Value);
        if (rel is null || !IsProtected(rel)) return next(http);
        if (OperatorAuthFilter.IsAuthorized(http, opt, env)
            || OperatorAuthFilter.IsSignedInAdmin(http, opt)
            || (http.User.Identity?.IsAuthenticated == true && IsUserOwnedAgentWrite(rel)))
        {
            return next(http);
        }
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return http.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            title = "Unauthorized",
            status = 401,
            detail = "Operator token required for this action (Authorization: Bearer <token> or X-Agent360-Operator-Token).",
        });
    }

    /// <summary>"/api/v1/agents/x" -> "agents/x"; null for non-API paths.</summary>
    internal static string? Relative(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var p = path.TrimStart('/');
        if (!p.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) return null;
        p = p[4..];
        if (p.Length > 1 && (p[0] == 'v' || p[0] == 'V') && char.IsDigit(p[1]))
        {
            var slash = p.IndexOf('/');
            p = slash < 0 ? "" : p[(slash + 1)..];
        }
        return p.TrimEnd('/');
    }

    internal static bool IsUserOwnedAgentWrite(string rel) =>
        rel.StartsWith("agents/", StringComparison.OrdinalIgnoreCase) &&
        (rel.EndsWith("/active-model", StringComparison.OrdinalIgnoreCase) ||
         rel.EndsWith("/compact-context", StringComparison.OrdinalIgnoreCase) ||
         rel.EndsWith("/toggle", StringComparison.OrdinalIgnoreCase) ||
         rel.EndsWith("/pin", StringComparison.OrdinalIgnoreCase) ||
         rel.Contains("/workspace/file-content", StringComparison.OrdinalIgnoreCase) ||
         rel.Contains("/webhooks/", StringComparison.OrdinalIgnoreCase) ||
         rel.Contains("/chats/", StringComparison.OrdinalIgnoreCase));

    internal static bool IsProtected(string rel)
    {
        foreach (var open in OpenWrites)
            if (rel.Equals(open, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(open + "/", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var prefix in ProtectedPrefixes)
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || rel.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.StartsWith("agents/", StringComparison.OrdinalIgnoreCase))
            foreach (var suffix in ProtectedAgentSuffixes)
                if (rel.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || rel.Contains(suffix + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
