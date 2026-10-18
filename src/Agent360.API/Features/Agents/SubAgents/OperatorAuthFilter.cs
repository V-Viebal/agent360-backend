using System.Security.Cryptography;
using System.Text;

namespace Agent360.API.Features.Agents.SubAgents;

public sealed class Agent360SecurityOptions
{
    public const string Section = "Agent360:Security";

    /// <summary>Shared operator token (Infisical core/prod:/agent360-a2a/app/runtime A2A_OPERATOR_TOKEN).</summary>
    public string OperatorToken { get; set; } = "";

    /// <summary>When false, mutating sub-agent/delegation routes are open (local development only).</summary>
    public bool RequireOperatorForWrites { get; set; } = true;

    /// <summary>Also protect POST/PUT/PATCH/DELETE on the agent runtime passthrough proxy.</summary>
    public bool ProtectRuntimeProxyWrites { get; set; }

    /// <summary>Require a Logto session (or operator token) for workspace file and chat-history reads.</summary>
    public bool RequireAuthForSensitiveReads { get; set; }

    /// <summary>Expose Scalar/OpenAPI outside Development (default off).</summary>
    public bool ExposeApiReference { get; set; }
}

/// <summary>
/// Fail-closed operator guard for mutating sub-agent and delegation routes.
/// Accepts "Authorization: Bearer &lt;token&gt;" or "X-Agent360-Operator-Token: &lt;token&gt;".
/// </summary>
public sealed class OperatorAuthFilter(
    Microsoft.Extensions.Options.IOptionsMonitor<Agent360SecurityOptions> options,
    IHostEnvironment env) : IEndpointFilter
{
    public const string HeaderName = "X-Agent360-Operator-Token";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
            return next(context);
        return IsAuthorized(http, options.CurrentValue, env)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(
                detail: "Operator token required for this action (Authorization: Bearer <token> or X-Agent360-Operator-Token).",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized"));
    }

    public static bool IsAuthorized(HttpContext http, Agent360SecurityOptions opt, IHostEnvironment env)
    {
        if (!opt.RequireOperatorForWrites) return true;
        if (string.IsNullOrWhiteSpace(opt.OperatorToken)) return env.IsDevelopment();
        var presented = http.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(presented))
        {
            var auth = http.Request.Headers.Authorization.FirstOrDefault();
            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) presented = auth[7..].Trim();
        }
        if (string.IsNullOrEmpty(presented)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(opt.OperatorToken.Trim()));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
