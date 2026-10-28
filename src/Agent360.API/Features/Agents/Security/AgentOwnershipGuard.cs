using System.Security.Claims;
using Agent360.API.Identity;
using Hmz.Core.SharedKernel.Authorization;
using Npgsql;

namespace Agent360.API.Features.Agents.Security;

public static class AgentOwnershipGuard
{
    public static string NormalizeAgentSlug(string? agentSlug) =>
        (agentSlug ?? "")
            .Replace("bot:agent360:", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToLowerInvariant();

    /// <summary>
    /// Verifies whether the current HTTP caller has full platform Admin clearance.
    /// IMPORTANT: If the caller is an authenticated user resolved in local PostgreSQL (currentUser.UserId &gt; 0),
    /// database RBAC (RoleId=2 ADMIN or Users.ManageRoles permission) is authoritative so that demoting an admin
    /// immediately revokes their fleet-wide access even if their old JWT or a browser header still exists.
    /// </summary>
    public static async Task<bool> IsEffectiveAdminAsync(
        HttpContext context,
        CurrentUser currentUser,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var user = context.User;
        var sub = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // 1. M2M service token is always system admin
        if (string.Equals(sub, "skyer-m2m-backend", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. If caller is an authenticated user with a resolved local DB UserId, PostgreSQL RBAC is authoritative!
        //    Do NOT allow X-Agent360-Operator-Token or stale JWT claims to override a demoted DB user.
        if (user.Identity?.IsAuthenticated == true && currentUser.UserId is > 0)
        {
            var snapshotAccessor = context.RequestServices.GetService<IPermissionSnapshotAccessor>();
            if (snapshotAccessor?.Snapshot.Permissions is { Count: > 0 } perms &&
                perms.Contains(Agent360.UserManagement.Domain.Constants.UserManagementPermissions.Users.ManageRoles))
            {
                return true;
            }

            // Direct DB check fallback if permission snapshot wasn't populated
            var connStr = config.GetConnectionString("DefaultConnection")
                ?? config.GetConnectionString("Postgres");
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                try
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync(ct);
                    await using var cmd = new NpgsqlCommand(
                        @"SELECT 1 FROM user_management.""UserRoles"" ur
                          JOIN user_management.""Roles"" r ON ur.""RoleId"" = r.""Id""
                          WHERE ur.""UserId"" = $1 AND UPPER(r.""Code"") IN ('ADMIN', 'SYSTEM')
                          LIMIT 1;", conn);
                    cmd.Parameters.AddWithValue(currentUser.UserId.Value);
                    var hasDbAdmin = await cmd.ExecuteScalarAsync(ct);
                    return hasDbAdmin is not null;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        // 3. Headless operator token (only when there is NO authenticated non-admin user session)
        var secOpt = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions>>()?.CurrentValue;
        if (user.Identity?.IsAuthenticated != true && secOpt is not null &&
            Agent360.API.Features.Agents.SubAgents.OperatorAuthFilter.PresentsOperatorToken(context, secOpt))
        {
            return true;
        }

        // 4. Configured AdminSubjects / Admin policy / JWT claims (when local DB user is not yet provisioned or in stateless test hosts)
        // Note: AuthorizeAsync(context.User, context, "Admin") passes HttpContext as resource so Program.cs can check headless operator token if unauthenticated.
        var authz = context.RequestServices.GetService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        if (authz is not null && (await authz.AuthorizeAsync(context.User, context, "Admin")).Succeeded)
        {
            // Only honor Admin policy here if user is unauthenticated (headless operator token / test host)
            // OR if user actually holds admin claims/subjects
            if (user.Identity?.IsAuthenticated != true)
            {
                return true;
            }
        }

        if (user.Identity?.IsAuthenticated == true)
        {
            var roles = user.FindAll("roles")
                .Concat(user.FindAll(ClaimTypes.Role))
                .Select(c => c.Value);

            if (roles.Any(r =>
                string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r, "administrator", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r, "system", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var scopes = user.FindAll("scope").Concat(user.FindAll("scp")).SelectMany(c => c.Value.Split(' '));
            if (scopes.Any(s => string.Equals(s, "admin:all", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (secOpt is not null && secOpt.IsAdminSubject(sub))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the set of normalized agent slugs assigned to the given userId in PostgreSQL.
    /// </summary>
    public static async Task<HashSet<string>> GetAssignedAgentSlugsAsync(
        long userId,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var assignedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (userId <= 0) return assignedSlugs;

        var connStr = config.GetConnectionString("DefaultConnection")
            ?? config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connStr)) return assignedSlugs;

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                @"SELECT ""AgentSlug"" FROM user_management.""AgentAssignments"" WHERE ""UserId"" = $1",
                conn);
            cmd.Parameters.AddWithValue(userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var slug = reader.GetString(0);
                assignedSlugs.Add(slug);
                var bare = NormalizeAgentSlug(slug);
                if (!string.IsNullOrWhiteSpace(bare))
                {
                    assignedSlugs.Add(bare);
                    assignedSlugs.Add($"bot:agent360:{bare}");
                }
            }
        }
        catch
        {
            // Fail-closed: empty set on error
        }

        return assignedSlugs;
    }

    public static async Task<(bool Allowed, string? Reason)> CheckAccessAsync(
        string agentSlug,
        HttpContext context,
        CurrentUser currentUser,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var cleanSlug = NormalizeAgentSlug(agentSlug);

        if (string.IsNullOrWhiteSpace(cleanSlug))
        {
            return (false, "Agent identifier is missing");
        }

        // 1. Check if caller has verified Admin clearance
        if (await IsEffectiveAdminAsync(context, currentUser, config, ct))
        {
            return (true, null);
        }

        // 1b. Allow signed file-download tokens (?sig=...&exp=...) for public artifact viewing
        var secOptForSig = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions>>()?.CurrentValue;
        if (secOptForSig is not null &&
            context.Request.Path.Value?.EndsWith("/workspace/file-download", StringComparison.OrdinalIgnoreCase) == true &&
            SensitiveReadGuardMiddleware.IsValidSignedFileToken(context, secOptForSig))
        {
            return (true, null);
        }

        // 2. Member user: must have assigned ownership in user_management."AgentAssignments"
        var userId = currentUser.UserId;
        if (userId is null || userId <= 0)
        {
            // In Development mode without an authenticated user session AND without DB configured, allow local dev test harness
            var env = context.RequestServices.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            if (env is not null && env.IsDevelopment() && context.User.Identity?.IsAuthenticated != true)
            {
                var secOpt = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions>>()?.CurrentValue;
                if (string.IsNullOrWhiteSpace(secOpt?.OperatorToken))
                {
                    return (true, null);
                }
            }

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
                  WHERE ""UserId"" = $1 AND (LOWER(""AgentSlug"") = $2 OR LOWER(""AgentSlug"") = $3)
                  LIMIT 1;", conn);

            cmd.Parameters.AddWithValue(userId.Value);
            cmd.Parameters.AddWithValue(cleanSlug);
            cmd.Parameters.AddWithValue($"bot:agent360:{cleanSlug}");

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

    public static async Task<IResult?> EnforceOrForbidAsync(
        string? agentSlug,
        HttpContext httpContext,
        CancellationToken ct = default)
    {
        var currentUser = httpContext.RequestServices.GetService<CurrentUser>()
            ?? new CurrentUser(new HttpContextAccessor { HttpContext = httpContext });
        var config = httpContext.RequestServices.GetService<IConfiguration>()
            ?? new ConfigurationBuilder().Build();

        var (allowed, reason) = await CheckAccessAsync(agentSlug ?? "", httpContext, currentUser, config, ct);
        if (allowed) return null;

        var isUnauthenticated = httpContext.User.Identity?.IsAuthenticated != true && (currentUser.UserId is null or <= 0);
        var status = isUnauthenticated ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        httpContext.Response.StatusCode = status;
        return Results.Problem(
            detail: reason ?? "Agent access denied",
            statusCode: status,
            title: isUnauthenticated ? "Unauthorized" : "Forbidden");
    }
}
