using System.Net.Http.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Tools;

public sealed class ToolManagementEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List all built-in tools
        routeBuilder.MapGet(
            "tools",
            async Task<IResult> (
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, "api/tools");

                var rawTools = await httpClient.GetFromJsonAsync<IReadOnlyList<QwenPawBuiltinToolDto>>(
                    requestUri,
                    cancellationToken);

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
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, $"api/tools/{Uri.EscapeDataString(toolName)}/toggle");

                var response = await httpClient.PostAsJsonAsync(
                    requestUri,
                    new { enabled = request.Enabled },
                    cancellationToken);

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
