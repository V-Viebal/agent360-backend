using System.Security.Cryptography;
using System.Text;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.SubAgents;
using Asp.Versioning;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>
/// Gate for the Selkies desktops (browser*.hmz.one / *.antares.vn). Selkies has no auth of its own and gives
/// every visitor its websocket, so Agent360 issues a signed, host-bound URL <c>https://host/a360/{token}/</c>.
/// Selkies derives its assets and websocket from <c>location.pathname</c>, so every request (including the
/// websocket upgrade) carries the token; Traefik forward-auths <c>/a360/</c> to <c>browser-auth/verify</c> and
/// strips the prefix. No cookies are involved, so it also works in iOS Safari iframes (third-party cookies blocked).
/// Tokens are HMAC-SHA256 over the operator secret, so no extra secret store is needed.
/// </summary>
public sealed class BrowserViewerAccess(IOptionsMonitor<Agent360SecurityOptions> security)
{
    public const string PathPrefix = "/a360/";
    internal static readonly TimeSpan SessionTtl = TimeSpan.FromHours(8);

    private byte[] Key()
    {
        var secret = security.CurrentValue.OperatorToken;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Agent360:Security:OperatorToken is required to sign browser viewer tokens");
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes("agent360-browser-viewer:" + secret.Trim()));
    }

    public bool CanSign => !string.IsNullOrWhiteSpace(security.CurrentValue.OperatorToken);

    public string Sign(string kind, string host, string mode, DateTimeOffset expires)
    {
        var payload = $"{kind}|{host.ToLowerInvariant()}|{mode}|{expires.ToUnixTimeSeconds()}";
        var mac = HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(payload));
        return B64(Encoding.UTF8.GetBytes(payload)) + "." + B64(mac);
    }

    /// <summary>Returns the mode ("control"/"watch") when the token is valid for this kind and host.</summary>
    public string? Verify(string? token, string kind, string host, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512 || !CanSign)
        {
            return null;
        }
        var dot = token.IndexOf('.');
        if (dot <= 0)
        {
            return null;
        }
        byte[] payloadBytes, mac;
        try
        {
            payloadBytes = UnB64(token[..dot]);
            mac = UnB64(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
        var expected = HMACSHA256.HashData(Key(), payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
        {
            return null;
        }
        var parts = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (parts.Length != 4 || parts[0] != kind || !string.Equals(parts[1], host, StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(parts[3], out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp) < now)
        {
            return null;
        }
        return parts[2] is "control" or "watch" ? parts[2] : null;
    }

    /// <summary>Token from a forwarded URI of the form <c>/a360/{token}/...</c>.</summary>
    public static string? TokenFromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = uri[PathPrefix.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        return end < 0 ? rest : rest[..end];
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

public sealed class BrowserViewerAccessEndpoint : IEndpoint
{
    public const string VerifyPath = "/api/v1/browser-auth/verify";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // Operator-only: the #shared/#collab role is chosen client-side by Selkies, so any desktop URL is a
        // potential controller. Non-operators keep the screenshot preview.
        routeBuilder.MapPost(
            "agents/{agentId}/browser/viewer",
            IResult (string agentId, HttpContext http, AgentBrowserDirectory directory, BrowserViewerAccess access,
                IOptionsMonitor<Agent360SecurityOptions> security, IHostEnvironment env) =>
            {
                if (!OperatorAuthFilter.IsAuthorized(http, security.CurrentValue, env))
                {
                    return Results.Problem(detail: "Operator token required to open the agent desktop.", statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
                }
                if (!access.CanSign)
                {
                    return Results.Problem(detail: "Browser viewer signing is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (!directory.TryResolve(agentId, out _, out _, out var viewerUrl, out _, out var profile) || profile is null || string.IsNullOrEmpty(viewerUrl))
                {
                    return TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser" });
                }
                var mode = string.Equals(http.Request.Query["mode"], "watch", StringComparison.OrdinalIgnoreCase) ? "watch" : "control";
                var host = new Uri(viewerUrl).Host;
                var token = access.Sign("session", host, mode, DateTimeOffset.UtcNow.Add(BrowserViewerAccess.SessionTtl));
                return TypedResults.Ok(new
                {
                    url = $"{viewerUrl.TrimEnd('/')}{BrowserViewerAccess.PathPrefix}{token}/",
                    mode,
                    expiresInSeconds = (int)BrowserViewerAccess.SessionTtl.TotalSeconds,
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("IssueAgentBrowserViewer")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Signed 8h URL for the agent's Selkies desktop (operator key required)")
            .AllowAnonymous();

        // Traefik forwardAuth target for /a360/{token}/ on every desktop host. Never proxies the desktop itself.
        routeBuilder.MapGet(
            "browser-auth/verify",
            IResult (HttpContext http, BrowserViewerAccess access) =>
            {
                var host = (http.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? "").Split(':')[0];
                var token = BrowserViewerAccess.TokenFromUri(http.Request.Headers["X-Forwarded-Uri"].FirstOrDefault());
                var mode = host.Length == 0 ? null : access.Verify(token, "session", host, DateTimeOffset.UtcNow);
                if (mode is null)
                {
                    return Results.StatusCode(StatusCodes.Status401Unauthorized);
                }
                http.Response.Headers["X-Agent360-Viewer-Mode"] = mode;
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok();
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("VerifyAgentBrowserViewer")
            .WithTags(EndpointTags.Agents)
            .ExcludeFromDescription()
            .AllowAnonymous();
    }
}
