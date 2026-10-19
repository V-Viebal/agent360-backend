using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agent360.API.Features.Agents.List;

public sealed class ListAgentsEndpoint : IEndpoint
{
    public const string RouteName = "ListAgents";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "agents",
            async Task<IResult> (
                HttpContext context,
                CurrentUser currentUser,
                IQwenPawClient client,
                IConfiguration config,
                IOptionsMonitor<Agent360SecurityOptions> secOptions,
                Microsoft.AspNetCore.Authorization.IAuthorizationService authorization,
                CancellationToken cancellationToken) =>
            {
                // Must be authenticated (either via Logto Bearer token, Operator Token, or Admin policy)
                var adminByPolicy = (await authorization.AuthorizeAsync(context.User, context, "Admin")).Succeeded;
                if (!adminByPolicy && context.User.Identity?.IsAuthenticated != true)
                {
                    return TypedResults.Unauthorized();
                }

                var allAgents = await client.GetAgentsAsync(cancellationToken);

                // 1. Check if caller has verified Admin clearance:
                // - Admin authorization policy (AdminSubjects allowlist, admin:all scope, or operator token)
                // - Role claims containing "admin" / "administrator"
                // - M2M service sub
                var user = context.User;
                var sub = user.FindFirst("sub")?.Value 
                    ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                var isConfigAdmin = secOptions.CurrentValue.IsAdminSubject(sub);

                var roles = user.FindAll("roles")
                    .Concat(user.FindAll(System.Security.Claims.ClaimTypes.Role))
                    .Select(c => c.Value);

                var isAdminRole = roles.Any(r => 
                    string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(r, "administrator", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r, "system", StringComparison.OrdinalIgnoreCase));

                var scopes = user.FindAll("scope").Concat(user.FindAll("scp")).SelectMany(c => c.Value.Split(' '));
                var hasAdminScope = scopes.Any(s => string.Equals(s, "admin:all", StringComparison.OrdinalIgnoreCase));

                var isM2M = string.Equals(sub, "skyer-m2m-backend", StringComparison.OrdinalIgnoreCase);

                var isFullAdmin = adminByPolicy || isConfigAdmin || isAdminRole || hasAdminScope || isM2M;

                // If user is Admin, return all agents across all infra nodes
                if (isFullAdmin)
                {
                    return TypedResults.Ok(allAgents);
                }

                // 2. Member caller: Strictly restrict to assigned agents only (Owner or Operator in AgentAssignments)
                var userId = currentUser.UserId;
                if (userId is null || userId <= 0)
                {
                    // Local user not resolved or has no DB record -> Return empty list immediately
                    return TypedResults.Ok(Array.Empty<AgentSummaryResponse>());
                }

                var assignedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var connStr = config.GetConnectionString("DefaultConnection") 
                    ?? config.GetConnectionString("Postgres");

                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    try
                    {
                        await using var conn = new NpgsqlConnection(connStr);
                        await conn.OpenAsync(cancellationToken);
                        await using var cmd = new NpgsqlCommand(
                            @"SELECT ""AgentSlug"" FROM user_management.""AgentAssignments"" WHERE ""UserId"" = $1", 
                            conn);
                        cmd.Parameters.AddWithValue(userId.Value);
                        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var slug = reader.GetString(0);
                            assignedSlugs.Add(slug);
                            // Also map bare slug without bot: prefix
                            if (slug.Contains(':'))
                            {
                                assignedSlugs.Add(slug.Split(':').Last());
                            }
                        }
                    }
                    catch
                    {
                        // Fail-safe: empty on DB error
                    }
                }

                // Filter down strictly to assigned agents only
                var filtered = allAgents
                    .Where(a => assignedSlugs.Contains(a.Slug) || assignedSlugs.Contains(a.Id))
                    .ToList();

                return TypedResults.Ok(filtered);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("List agents (Infra-wide for Admin, Assigned-only for Members)")
            .WithDescription("Retrieves the list of active agents: all infra agents if caller is admin, or only owned/assigned agents if member.")
            .Produces<IReadOnlyList<AgentSummaryResponse>>()
            .AllowAnonymous();
    }
}
