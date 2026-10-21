using System.Security.Claims;
using Agent360.API.Identity;
using Npgsql;

namespace Agent360.API.Features.Agents.Security;

public static class AgentOwnershipGuard
{
    public static async Task<(bool Allowed, string? Reason)> CheckAccessAsync(
        string agentSlug,
        HttpContext context,
        CurrentUser currentUser,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var cleanSlug = (agentSlug ?? "")
            .Replace("bot:agent360:", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(cleanSlug))
        {
            return (false, "Agent identifier is missing");
        }

        var env = context.RequestServices.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        var secOpt = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions>>()?.CurrentValue;

        // In Development mode, bypass ownership check for local testing
        if (env is not null && env.IsDevelopment())
        {
            return (true, null);
        }

        // Check if caller presents operator token
        if (secOpt is not null && Agent360.API.Features.Agents.SubAgents.OperatorAuthFilter.PresentsOperatorToken(context, secOpt))
        {
            return (true, null);
        }

        // The Admin policy is the single definition of a platform admin: operator token, Logto admin role/scope,
        // M2M service token and configured AdminSubjects. Without this the operator console / A2A control plane
        // (operator token, no Logto session) got 403 on every per-agent route.
        var authz = context.RequestServices.GetService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        if (authz is not null && (await authz.AuthorizeAsync(context.User, context, "Admin")).Succeeded)
        {
            return (true, null);
        }

        var user = context.User;
        // 1. Check if caller is Admin / System
        var roles = user.FindAll("roles")
            .Concat(user.FindAll(ClaimTypes.Role))
            .Select(c => c.Value);

        var isAdmin = roles.Any(r =>
            string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, "administrator", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, "system", StringComparison.OrdinalIgnoreCase));

        var scopes = user.FindAll("scope").Concat(user.FindAll("scp")).SelectMany(c => c.Value.Split(' '));
        if (scopes.Any(s => string.Equals(s, "admin:all", StringComparison.OrdinalIgnoreCase)))
        {
            isAdmin = true;
        }

        var sub = user.FindFirst("sub")?.Value;
        if (string.Equals(sub, "skyer-m2m-backend", StringComparison.OrdinalIgnoreCase) ||
            (secOpt is not null && secOpt.IsAdminSubject(sub)))
        {
            isAdmin = true;
        }

        if (isAdmin)
        {
            return (true, null);
        }

        // 2. Member user: must have assigned ownership in user_management."AgentAssignments"
        var userId = currentUser.UserId;
        if (userId is null || userId <= 0)
        {
            return (false, "User session not authenticated or local profile missing");
        }

        var connStr = config.GetConnectionString("DefaultConnection") 
            ?? config.GetConnectionString("Postgres");

        if (string.IsNullOrWhiteSpace(connStr))
        {
            return (false, "Database connection is not configured to verify agent ownership");
        }

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                @"SELECT 1 FROM user_management.""AgentAssignments"" 
                  WHERE ""UserId"" = $1 AND LOWER(""AgentSlug"") = $2 
                  LIMIT 1;", conn);

            cmd.Parameters.AddWithValue(userId.Value);
            cmd.Parameters.AddWithValue(cleanSlug);

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result != null)
            {
                return (true, null);
            }
        }
        catch (Exception ex)
        {
            // Fail-closed on database check error
            return (false, $"Unable to verify agent ownership due to access check error: {ex.Message}");
        }

        return (false, $"You are not assigned as owner or operator for agent '{cleanSlug}'. Contact an administrator to grant access.");
    }
}
