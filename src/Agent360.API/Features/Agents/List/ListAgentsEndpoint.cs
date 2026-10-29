using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Endpoints;
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
                AgentProfileStore profileStore,
                IConfiguration config,
                IOptionsMonitor<Agent360SecurityOptions> secOptions,
                Microsoft.AspNetCore.Authorization.IAuthorizationService authorization,
                CancellationToken cancellationToken) =>
            {
                var isEffectiveAdmin = await AgentOwnershipGuard.IsEffectiveAdminAsync(context, currentUser, config, cancellationToken);
                var hasTokenHeader = context.Request.Headers.Authorization.FirstOrDefault()?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true;

                var rawAgents = await client.GetAgentsAsync(cancellationToken);
                var allAgents = rawAgents.Select(a =>
                {
                    var custom = profileStore.Get(a.Slug) ?? profileStore.Get(a.Id);
                    if (custom is null) return a;
                    var customName = custom["name"]?.GetValue<string>();
                    var customAvatar = custom["avatar"];
                    return a with
                    {
                        Name = !string.IsNullOrWhiteSpace(customName) ? customName : a.Name,
                        AvatarConfig = customAvatar is not null ? customAvatar.DeepClone() : a.AvatarConfig
                    };
                }).ToList();

                // 1. If caller has verified Admin clearance, return all agents across all infra nodes
                if (isEffectiveAdmin)
                {
                    return TypedResults.Ok((IReadOnlyList<AgentSummaryResponse>)allAgents);
                }

                // 2. Member caller: Strictly restrict to assigned agents or Anyone-enabled agents (-1) in AgentAssignments
                var userId = currentUser.UserId ?? 0L;
                var assignedSlugs = await AgentOwnershipGuard.GetAssignedAgentSlugsAsync(userId, config, cancellationToken);
                if (assignedSlugs.Count == 0)
                {
                    if (!isEffectiveAdmin && context.User.Identity?.IsAuthenticated != true && !hasTokenHeader)
                    {
                        return TypedResults.Unauthorized();
                    }
                    return TypedResults.Ok(Array.Empty<AgentSummaryResponse>());
                }

                // Filter down strictly to assigned agents only
                var filtered = allAgents
                    .Where(a => assignedSlugs.Contains(a.Slug) || assignedSlugs.Contains(a.Id))
                    .ToList();

                return TypedResults.Ok((IReadOnlyList<AgentSummaryResponse>)filtered);
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
