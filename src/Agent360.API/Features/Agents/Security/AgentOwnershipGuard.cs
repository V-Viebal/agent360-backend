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
        if (string.Equals(sub, "skyer-m2m-backend", StringComparison.OrdinalIgnoreCase))
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
            return (true, null); // Fallback open if db is missing
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
        catch
        {
            // Fallback allow if error executing check
            return (true, null);
        }

        return (false, $"You are not assigned as owner or operator for agent '{cleanSlug}'. Contact an administrator to grant access.");
    }
}
