using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Mcps;

public sealed class McpManagementEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List configured MCP servers
        routeBuilder.MapGet(
            "mcps",
            async Task<IResult> (
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, "api/mcp");

                var list = new List<McpServerSummaryResponse>();

                try
                {
                    var rawMcps = await httpClient.GetFromJsonAsync<IReadOnlyList<QwenPawMcpDto>>(
                        requestUri,
                        cancellationToken);

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
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, $"api/mcp/{Uri.EscapeDataString(mcpKey)}/toggle");

                var response = await httpClient.PostAsJsonAsync(
                    requestUri,
                    new { enabled = request.Enabled },
                    cancellationToken);

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
