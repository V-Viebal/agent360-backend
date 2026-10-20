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

    /// <summary>
    /// Logto user ids (JWT <c>sub</c>) that hold the Agent360 Admin policy, ';' or ',' separated. Logto access tokens
    /// for this resource carry no role/scope claims, so platform admins are bound by subject.
    /// </summary>
    public string AdminSubjects { get; set; } = "";

    public bool IsAdminSubject(string? sub) =>
        !string.IsNullOrWhiteSpace(sub) &&
        AdminSubjects.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => string.Equals(s, sub, StringComparison.Ordinal));
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
        return IsAuthorized(http, options.CurrentValue, env) || IsSignedInAdmin(http, options.CurrentValue)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(
                detail: "Operator token required for this action (Authorization: Bearer <token> or X-Agent360-Operator-Token).",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized"));
    }

    /// <summary>
    /// A validated Logto session (JwtBearer already ran) whose subject is listed in AdminSubjects: the same people
    /// who hold the Admin policy may run sub-agent/delegation writes from the Skyer UI without an operator key.
    /// </summary>
    public static bool IsSignedInAdmin(HttpContext http, Agent360SecurityOptions opt) =>
        http.User.Identity?.IsAuthenticated == true && opt.IsAdminSubject(http.User.FindFirst("sub")?.Value
            ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);

    /// <summary>True only when a configured operator token is presented (never true by configuration alone).</summary>
    public static bool PresentsOperatorToken(HttpContext http, Agent360SecurityOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.OperatorToken)) return false;
        var presented = http.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(presented))
        {
            var auth = http.Request.Headers.Authorization.FirstOrDefault();
            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) presented = auth[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(presented) && http.Request.Query.TryGetValue("operatorToken", out var qOp) && !string.IsNullOrWhiteSpace(qOp))
        {
            presented = qOp.ToString().Trim();
        }
        if (string.IsNullOrWhiteSpace(presented) && http.Request.Query.TryGetValue("operator_token", out var qOp2) && !string.IsNullOrWhiteSpace(qOp2))
        {
            presented = qOp2.ToString().Trim();
        }
        if (string.IsNullOrEmpty(presented)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(opt.OperatorToken.Trim()));
        return CryptographicOperations.FixedTimeEquals(a, b);
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
        if (string.IsNullOrWhiteSpace(presented) && http.Request.Query.TryGetValue("operatorToken", out var qOp) && !string.IsNullOrWhiteSpace(qOp))
        {
            presented = qOp.ToString().Trim();
        }
        if (string.IsNullOrWhiteSpace(presented) && http.Request.Query.TryGetValue("operator_token", out var qOp2) && !string.IsNullOrWhiteSpace(qOp2))
        {
            presented = qOp2.ToString().Trim();
        }
        if (string.IsNullOrEmpty(presented)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(opt.OperatorToken.Trim()));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
