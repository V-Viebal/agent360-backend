using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Mcps;

public sealed class McpManagementEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List configured MCP servers
        routeBuilder.MapGet(
            "mcps",
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
                var list = new List<McpServerSummaryResponse>();

                try
                {
                    var target = routing.ResolveTarget(agentId);
                    using var response = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/mcp", timeout: TimeSpan.FromSeconds(30), ct: cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var rawMcps = await response.Content.ReadFromJsonAsync<IReadOnlyList<QwenPawMcpDto>>(cancellationToken);

                    if (rawMcps is not null)
                    {
                        list.AddRange(rawMcps.Select(m => new McpServerSummaryResponse(
                            Key: m.Key,
                            Name: m.Name,
                            Description: m.Description ?? "",
                            Enabled: m.Enabled,
                            Transport: m.Transport ?? "stdio",
                            Command: m.Command,
                            Url: m.Url
                        )));
                    }
                }
                catch
                {
                    // Fallback to local configuration
                }

                // Fallback: If list has no MCPs for the target agent (e.g. non-QwenPaw swarm, edge node, or network blip),
                // query Agent Guardian's verified discovery cache so edge and non-QwenPaw runtimes expose their verified tools
                if (list.Count == 0 && !string.IsNullOrWhiteSpace(agentId))
                {
                    try
                    {
                        var cleanSlug = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
                        var client = httpContext?.RequestServices.GetService<IQwenPawClient>();
                        if (client != null)
                        {
                            var agents = await client.GetAgentsAsync(cancellationToken);
                            var matched = agents.FirstOrDefault(a => a.Slug.Equals(cleanSlug, StringComparison.OrdinalIgnoreCase) || a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
                            if (matched?.Tools != null && matched.Tools.Count > 0)
                            {
                                list.AddRange(matched.Tools.Where(t => t.Type == "mcp").Select(t => new McpServerSummaryResponse(
                                    Key: t.Name,
                                    Name: t.Name,
                                    Description: t.Description ?? "",
                                    Enabled: true,
                                    Transport: "gateway",
                                    Command: null,
                                    Url: null
                                )));
                            }
                        }
                    }
                    catch
                    {
                        // Fallback gracefully
                    }
                }

                // Ensure workspace-registered MCPs like Composio appear in the catalog
                if (!list.Any(m => m.Key == "composio"))
                {
                    list.Insert(0, new McpServerSummaryResponse(
                        Key: "composio",
                        Name: "Composio Connect MCP Hub",
                        Description: "Official Composio MCP Server connecting 1,500+ tools (Gmail, Notion, Todoist, Canva, etc.)",
                        Enabled: true,
                        Transport: "stdio",
                        Command: "node .agent/mcps/composio-mcp-server/index.js",
                        Url: "https://connect.composio.dev/mcp"
                    ));
                }

                return TypedResults.Ok(list);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListMcpServers")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all registered Model Context Protocol (MCP) servers")
            .Produces<IReadOnlyList<McpServerSummaryResponse>>()
            .AllowAnonymous();

        // 2. Toggle MCP server enabled state
        routeBuilder.MapPatch(
            "mcps/{mcpKey}/toggle",
            async Task<IResult> (
                string mcpKey,
                [FromBody] ToggleMcpRequest request,
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                CancellationToken cancellationToken) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, cancellationToken) is { } denied) return denied;
                var target = routing.ResolveTarget(agentId);
                var basePath = $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}";
                using (var current = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"{basePath}/mcp/{Uri.EscapeDataString(mcpKey)}", timeout: TimeSpan.FromSeconds(30), ct: cancellationToken))
                {
                    current.EnsureSuccessStatusCode();
                    var node = await current.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>(cancellationToken);
                    var enabled = node?["enabled"]?.GetValue<bool>();
                    if (enabled == request.Enabled)
                    {
                        return TypedResults.Ok(new { mcp = mcpKey, enabled = request.Enabled });
                    }
                }

                using var response = await routing.SendAsync(target.Instance!, HttpMethod.Patch, $"{basePath}/mcp/toggle/{Uri.EscapeDataString(mcpKey)}", ct: cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return TypedResults.Ok(new { mcp = mcpKey, enabled = request.Enabled });
                }

                return TypedResults.Problem($"Failed to toggle MCP server state: {response.StatusCode}");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ToggleMcpServer")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Enable or disable an MCP server integration")
            .AllowAnonymous();
    }
}
