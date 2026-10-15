using System.Net.Http.Json;
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

                var rawMcps = await httpClient.GetFromJsonAsync<IReadOnlyList<QwenPawMcpDto>>(
                    requestUri,
                    cancellationToken);

                if (rawMcps is null)
                {
                    return TypedResults.Ok(Array.Empty<McpServerSummaryResponse>());
                }

                var result = rawMcps.Select(m => new McpServerSummaryResponse(
                    Key: m.Key,
                    Name: m.Name,
                    Description: m.Description ?? "",
                    Enabled: m.Enabled,
                    Transport: m.Transport ?? "stdio",
                    Command: m.Command,
                    Url: m.Url
                )).ToList();

                return TypedResults.Ok(result);
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
