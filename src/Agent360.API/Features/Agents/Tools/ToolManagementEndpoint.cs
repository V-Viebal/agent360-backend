using System.Net.Http.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Tools;

public sealed class ToolManagementEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List all built-in tools
        routeBuilder.MapGet(
            "tools",
            async Task<IResult> (
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                CancellationToken cancellationToken) =>
            {
                if (!string.IsNullOrWhiteSpace(agentId) && httpContext.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, cancellationToken) is { } denied)
                {
                    return denied;
                }
                var target = routing.ResolveTarget(agentId);
                using var response = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/tools", ct: cancellationToken);
                response.EnsureSuccessStatusCode();
                var rawTools = await response.Content.ReadFromJsonAsync<IReadOnlyList<QwenPawBuiltinToolDto>>(cancellationToken);

                if (rawTools is null)
                {
                    return TypedResults.Ok(Array.Empty<BuiltinToolSummaryResponse>());
                }

                var result = rawTools.Select(t => new BuiltinToolSummaryResponse(
                    Name: t.Name,
                    Description: t.Description ?? "",
                    Enabled: t.Enabled,
                    AsyncExecution: t.AsyncExecution,
                    Icon: t.Icon,
                    RequiresConfig: t.RequiresConfig
                )).ToList();

                return TypedResults.Ok(result);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListBuiltinTools")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all built-in agent tools")
            .Produces<IReadOnlyList<BuiltinToolSummaryResponse>>()
            .AllowAnonymous();

        // 2. Toggle tool enabled/disabled state
        routeBuilder.MapPatch(
            "tools/{toolName}/toggle",
            async Task<IResult> (
                string toolName,
                [FromBody] ToggleToolRequest request,
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                CancellationToken cancellationToken) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, cancellationToken) is { } denied) return denied;
                var target = routing.ResolveTarget(agentId);
                var basePath = $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}";
                using (var current = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"{basePath}/tools", timeout: TimeSpan.FromSeconds(30), ct: cancellationToken))
                {
                    current.EnsureSuccessStatusCode();
                    var node = await current.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>(cancellationToken);
                    var enabled = node?.AsArray().FirstOrDefault(t => t?["name"]?.GetValue<string>() == toolName)?["enabled"]?.GetValue<bool>();
                    if (enabled == request.Enabled)
                    {
                        return TypedResults.Ok(new { tool = toolName, enabled = request.Enabled });
                    }
                }

                using var response = await routing.SendAsync(target.Instance!, HttpMethod.Patch, $"{basePath}/tools/{Uri.EscapeDataString(toolName)}/toggle", ct: cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return TypedResults.Ok(new { tool = toolName, enabled = request.Enabled });
                }

                return TypedResults.Problem($"Failed to update tool toggle status: {response.StatusCode}");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ToggleBuiltinTool")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Enable or disable a built-in agent tool")
            .AllowAnonymous();
    }
}
