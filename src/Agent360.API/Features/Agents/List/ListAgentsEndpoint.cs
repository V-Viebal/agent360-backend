using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Identity;
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
                CancellationToken cancellationToken) =>
            {
                var allAgents = await client.GetAgentsAsync(cancellationToken);

                // 1. Check if caller has Admin role / claim / token
                var user = context.User;
                var roles = user.FindAll("roles")
                    .Concat(user.FindAll(System.Security.Claims.ClaimTypes.Role))
                    .Select(c => c.Value);

                var isAdmin = roles.Any(r => 
                    string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(r, "administrator", StringComparison.OrdinalIgnoreCase));

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

                // If user is Admin, return all agents across all infra nodes
                if (isAdmin)
                {
                    return TypedResults.Ok(allAgents);
                }

                // 2. Member caller: Only return agents where the member is assigned as Owner/Operator
                var userId = currentUser.UserId;
                if (userId is null || userId <= 0)
                {
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
                            "SELECT \"AgentSlug\" FROM user_management.\"AgentAssignments\" WHERE \"UserId\" = $1", 
                            conn);
                        cmd.Parameters.AddWithValue(userId.Value);
                        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            assignedSlugs.Add(reader.GetString(0));
                        }
                    }
                    catch
                    {
                        // Fallback gracefully
                    }
                }

                var filtered = allAgents
                    .Where(a => assignedSlugs.Contains(a.Slug) || assignedSlugs.Contains(a.Id))
                    .ToList();

                return TypedResults.Ok(filtered);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("List agents (Infra-wide for Admin, Assigned-only for Members)")
            .WithDescription("Retrieves the list of active agents: all infra agents if caller is admin, or only owned agents if member.")
            .Produces<IReadOnlyList<AgentSummaryResponse>>()
            .RequireAuthorization();
    }
}
