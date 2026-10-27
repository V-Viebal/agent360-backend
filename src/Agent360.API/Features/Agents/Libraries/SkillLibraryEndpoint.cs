using Agent360.API.Features.Agents.Services;
using System.Net.Http.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Libraries;

public sealed class SkillLibraryEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. Get all skills across agent library / platform (supports multi-node scoping via agentId)
        routeBuilder.MapGet(
            "libraries/skills",
            async Task<IResult> (
                [FromQuery] string? agentId,
                IQwenPawClient qwenPawClient,
                CancellationToken cancellationToken) =>
            {
                var rawSkills = await qwenPawClient.GetSkillsAsync(agentId, cancellationToken);

                if (rawSkills is null)
                {
                    return TypedResults.Ok(Array.Empty<SkillLibraryItemResponse>());
                }

                var result = rawSkills.Select(s => new SkillLibraryItemResponse(
                    Name: s.Name,
                    Description: s.Description ?? "",
                    Source: s.Source ?? "custom",
                    Emoji: s.Emoji,
                    Enabled: s.Enabled,
                    Channels: s.Channels ?? Array.Empty<string>(),
                    Tags: s.Tags ?? Array.Empty<string>(),
                    LastUpdated: s.LastUpdated
                )).ToList();

                return TypedResults.Ok(result);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListSkillsLibrary")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all installed capabilities and skills in the agent library")
            .Produces<IReadOnlyList<SkillLibraryItemResponse>>()
            .AllowAnonymous();

        // 2. Toggle skill enabled/disabled status
        routeBuilder.MapPatch(
            "libraries/skills/{skillName}/toggle",
            async Task<IResult> (
                string skillName,
                [FromBody] ToggleSkillRequest request,
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                CancellationToken cancellationToken) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, cancellationToken) is { } denied) return denied;
                var target = routing.ResolveTarget(agentId);
                var action = request.Enabled ? "enable" : "disable";
                using var response = await routing.SendAsync(target.Instance!, HttpMethod.Post, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/skills/{Uri.EscapeDataString(skillName)}/{action}", ct: cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return TypedResults.Ok(new { skill = skillName, enabled = request.Enabled });
                }

                return TypedResults.Problem($"Failed to update skill toggle status on infra runtime: {response.StatusCode}");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ToggleSkillLibrary")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Enable or disable a skill in the agent library")
            .AllowAnonymous();
    }
}
