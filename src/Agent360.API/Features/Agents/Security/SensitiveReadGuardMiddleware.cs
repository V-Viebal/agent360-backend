using System.Security.Cryptography;
using System.Text;
using Agent360.API.Features.Agents.SubAgents;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Security;

/// <summary>
/// When Agent360:Security:RequireAuthForSensitiveReads is on, reads of workspace files/trees, chat lists and chat
/// histories, and the agent browser (tabs, screenshots, screencast) need a valid Logto access token (JwtBearer, audience = API resource) or the operator token.
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
        var hasBearer = http.Request.Headers.Authorization.FirstOrDefault()?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true;
        if (http.User.Identity?.IsAuthenticated == true || OperatorAuthFilter.IsAuthorized(http, opt, env) || hasBearer) return next(http);

        // Allow signed file-download tokens for mobile / external viewing without cookies: ?sig=...&exp=...
        if (rel.EndsWith("/workspace/file-download", StringComparison.OrdinalIgnoreCase) && IsValidSignedFileToken(http, opt))
        {
            return next(http);
        }
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

    internal static bool IsValidSignedFileToken(HttpContext http, Agent360SecurityOptions opt)
    {
        var query = http.Request.Query;
        if (!query.TryGetValue("sig", out var qSig) || !query.TryGetValue("exp", out var qExp) || !query.TryGetValue("path", out var qPath))
        {
            return false;
        }

        if (!long.TryParse(qExp.ToString(), out var expUnix) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expUnix)
        {
            return false;
        }

        var secret = string.IsNullOrWhiteSpace(opt.OperatorToken) ? "skyer-file-download-token" : opt.OperatorToken;
        var cleanPath = qPath.ToString().Trim().Replace('\\', '/');
        var payload = $"{cleanPath}:{expUnix}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedHex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var expectedShort = expectedHex[..Math.Min(16, expectedHex.Length)];

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(qSig.ToString().ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedShort));
    }
    internal static bool IsSensitive(string rel)
    {
        var p = rel.ToLowerInvariant();
        if (!p.StartsWith("agents/") && !p.StartsWith("chats/") && !p.StartsWith("shared/")) return false;
        if (p.StartsWith("shared/")) return false; // explicit share links are public by design
        // Realtime SSE event streams and channel telemetry endpoints manage their own stream life-cycle
        if ((p.Contains("chats/") && p.EndsWith("/stream")) || p == "chats/stream" || p == "chats/ws" || p.Contains("channels/responses") || p == "chats/active" || p.EndsWith("/chats/active") || p.Contains("ws/")) return false;
        var parts = p.Split('/');
        // agents/workspace/..., agents/chats/{id}, agents/{id}/chats[/...], agents/{id}/workspace/(tree|file-content|...)
        if (parts.Length >= 2 && parts[0] == "agents" && (parts[1] == "workspace" || parts[1] == "chats" || parts[1] == "sessions")) return true;
        if (parts.Length >= 3 && parts[0] == "agents")
        {
            var sub = parts[2];
            if (sub == "chats") return true;
            if (sub == "workspace" && parts.Length >= 4 && parts[3] is "tree" or "file-content" or "file-download" or "file-preview" or "code-files" or "system-prompt-files") return true;
            if (sub == "memory") return true;
            // The agent's browser: tab titles/URLs, screenshots and the live screencast. Only the value-free
            // health probe (cdpReachable/profileWindowOpen) stays public for monitoring.
            if (sub == "browser") return !(parts.Length == 4 && parts[3] == "health");
        }
        return parts[0] == "chats";
    }
}
