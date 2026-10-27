using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Identity;
using Microsoft.Extensions.Options;

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
                // Must be authenticated (either via Logto Bearer token, headless Operator Token, or Admin policy)
                var isEffectiveAdmin = await AgentOwnershipGuard.IsEffectiveAdminAsync(context, currentUser, config, cancellationToken);
                if (!isEffectiveAdmin && context.User.Identity?.IsAuthenticated != true)
                {
                    return TypedResults.Unauthorized();
                }

                var allAgents = await client.GetAgentsAsync(cancellationToken);

                // 1. If caller has verified Admin clearance, return all agents across all infra nodes
                if (isEffectiveAdmin)
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

                var assignedSlugs = await AgentOwnershipGuard.GetAssignedAgentSlugsAsync(userId.Value, config, cancellationToken);
                if (assignedSlugs.Count == 0)
                {
                    return TypedResults.Ok(Array.Empty<AgentSummaryResponse>());
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
