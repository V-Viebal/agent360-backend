using System.Text.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Integrations;

public sealed class ComposioIntegrationEndpoint : IEndpoint
{
    private const string ComposioMcpUrl = "https://connect.composio.dev/mcp";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. Get MCP clients for an agent (proxy to QwenPaw /api/mcp with X-Agent-Id)
        routeBuilder.MapGet(
            "mcp",
            async (
                [FromHeader(Name = "X-Agent-Id")] string? agentIdHeader,
                [FromQuery] string? agentIdQuery,
                HttpClient httpClient,
                IFleetInstanceRoutingService routing,
                CancellationToken ct) =>
            {
                var targetAgent = !string.IsNullOrWhiteSpace(agentIdHeader) 
                    ? agentIdHeader 
                    : (!string.IsNullOrWhiteSpace(agentIdQuery) ? agentIdQuery : "mons");
                
                if (targetAgent.Contains(':'))
                {
                    targetAgent = targetAgent.Split(':').Last();
                }
                var target = routing.ResolveTarget(targetAgent);
                var requestUri = AgentUri(target, "mcp");

                using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
                req.Headers.Add("X-Agent-Id", targetAgent);

                using var resp = await SendRoutedAsync(httpClient, routing, targetAgent, req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return Results.StatusCode((int)resp.StatusCode);
                }

                var json = await resp.Content.ReadFromJsonAsync<JsonNode>(ct);
                return Results.Ok(json);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentMcpClients")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List MCP clients configured on an agent")
            .AllowAnonymous();

        // 2. Connect Composio Tool / App: Registers MCP driver card and materializes dedicated Skill with Dynamic Schema Tool Discovery
        routeBuilder.MapPost(
            "integrations/composio/connect",
            async (
                [FromBody] ComposioConnectRequest request,
                HttpClient httpClient,
                IFleetInstanceRoutingService routing,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.AppSlug))
                {
                    return Results.BadRequest(new { error = "appSlug is required" });
                }

                var targetAgent = !string.IsNullOrWhiteSpace(request.AgentId) ? request.AgentId : "mons";
                if (targetAgent.Contains(':'))
                {
                    targetAgent = targetAgent.Split(':').Last();
                }

                // Resolve per-agent isolated credential from Infisical / process environment
                var effectiveToken = InfisicalCredentialAuditService.ResolveAgentComposioToken(targetAgent);
                var target = routing.ResolveTarget(targetAgent);

                var clientKey = $"composio_{request.AppSlug.ToLowerInvariant().Trim()}";
                var displayName = request.AppSlug.ToLowerInvariant().Trim();

                // 2a. Call QwenPaw to register the MCP Driver Card
                var mcpCreateBody = new
                {
                    client_key = clientKey,
                    client = new
                    {
                        name = displayName,
                        description = $"Composio {displayName} integration for {targetAgent}",
                        transport = "sse",
                        url = ComposioMcpUrl,
                        headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = $"Bearer {effectiveToken}"
                        },
                        enabled = true
                    }
                };

                using var mcpReq = new HttpRequestMessage(HttpMethod.Post, AgentUri(target, "mcp"))
                {
                    Content = JsonContent.Create(mcpCreateBody)
                };
                mcpReq.Headers.Add("X-Agent-Id", targetAgent);

                var mcpResp = await SendRoutedAsync(httpClient, routing, targetAgent, mcpReq, ct);
                if (!mcpResp.IsSuccessStatusCode && mcpResp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    using var toggleReq = new HttpRequestMessage(HttpMethod.Patch, AgentUri(target, $"mcp/toggle/{Uri.EscapeDataString(clientKey)}"));
                    toggleReq.Headers.Add("X-Agent-Id", targetAgent);
                    await SendRoutedAsync(httpClient, routing, targetAgent, toggleReq, ct);
                }

                // 2b. Dynamic Schema Discovery from Composio SSE endpoint (tools/call COMPOSIO_SEARCH_TOOLS)
                var discoveredFunctions = await DiscoverComposioToolsAsync(httpClient, displayName, effectiveToken, ct);

                // 2c. Materialize skill in agent workspace with ## Available Functions populated
                var skillName = $"composio-{displayName}";
                var functionsMarkdown = FormatFunctionsMarkdown(discoveredFunctions);

                var skillContent = $@"---
name: {skillName}
description: Autonomous integration skill for {displayName} powered by Composio MCP tools.
status: active
owner: agent360
version: 1.0.0
---

# {displayName.ToUpperInvariant()} Integration Skill (Composio)

This skill provides autonomous capabilities for interacting with {displayName} through the connected Composio MCP driver.

## Available Tool Protocol
- **Transport**: Remote SSE via Composio Hub ({ComposioMcpUrl})
- **MCP Client Key**: `{clientKey}`
- **Discovered Functions Count**: {discoveredFunctions.Count}

## Available Functions
{functionsMarkdown}

## Usage Guidelines
1. When a task requires {displayName} actions, inspect available functions from `{clientKey}`.
2. Execute requests with verified parameter schemas matching the discovered endpoints above.
3. Handle return receipts and escalate authorization prompts when required.
";

                var createSkillBody = new
                {
                    name = skillName,
                    content = skillContent,
                    enable = true
                };

                using var skillReq = new HttpRequestMessage(HttpMethod.Post, AgentUri(target, "skills"))
                {
                    Content = JsonContent.Create(createSkillBody)
                };
                skillReq.Headers.Add("X-Agent-Id", targetAgent);

                var skillResp = await SendRoutedAsync(httpClient, routing, targetAgent, skillReq, ct);
                var skillCreated = skillResp.IsSuccessStatusCode;

                // If already existed (409 conflict), update via PUT /api/skills/save
                if (!skillCreated && skillResp.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    var saveSkillBody = new
                    {
                        name = skillName,
                        content = skillContent,
                        overwrite = true
                    };
                    using var saveReq = new HttpRequestMessage(HttpMethod.Put, AgentUri(target, "skills/save"))
                    {
                        Content = JsonContent.Create(saveSkillBody)
                    };
                    saveReq.Headers.Add("X-Agent-Id", targetAgent);
                    var saveResp = await SendRoutedAsync(httpClient, routing, targetAgent, saveReq, ct);
                    skillCreated = saveResp.IsSuccessStatusCode;
                }

                // Ensure skill is enabled
                try
                {
                    using var enableReq = new HttpRequestMessage(HttpMethod.Post, AgentUri(target, $"skills/{Uri.EscapeDataString(skillName)}/enable"));
                    enableReq.Headers.Add("X-Agent-Id", targetAgent);
                    await SendRoutedAsync(httpClient, routing, targetAgent, enableReq, ct);
                }
                catch
                {
                    // Ignore toggle err if already enabled
                }

                return Results.Ok(new
                {
                    success = true,
                    client_key = clientKey,
                    agent_id = targetAgent,
                    skill_name = skillName,
                    skill_provisioned = skillCreated,
                    tools_discovered_count = discoveredFunctions.Count,
                    message = $"Successfully connected {displayName} to {targetAgent} and provisioned skill {skillName} with {discoveredFunctions.Count} discovered functions"
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ConnectComposioApp")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Connect and apply Composio application and provision matching skill with discovered schema")
            .AllowAnonymous();

        // 3. Refresh Tool Schema on-demand: Re-discovers tools via SSE and hot-updates SKILL.md
        routeBuilder.MapPost(
            "integrations/composio/refresh-schema",
            async (
                [FromBody] ComposioRefreshSchemaRequest request,
                HttpClient httpClient,
                IFleetInstanceRoutingService routing,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.AppSlug))
                {
                    return Results.BadRequest(new { error = "appSlug is required" });
                }

                var targetAgent = !string.IsNullOrWhiteSpace(request.AgentId) ? request.AgentId : "mons";
                if (targetAgent.Contains(':'))
                {
                    targetAgent = targetAgent.Split(':').Last();
                }

                var effectiveToken = InfisicalCredentialAuditService.ResolveAgentComposioToken(targetAgent);
                var target = routing.ResolveTarget(targetAgent);

                var clientKey = $"composio_{request.AppSlug.ToLowerInvariant().Trim()}";
                var displayName = request.AppSlug.ToLowerInvariant().Trim();

                // Dynamic Re-Discovery from SSE
                var discoveredFunctions = await DiscoverComposioToolsAsync(httpClient, displayName, effectiveToken, ct);

                var skillName = $"composio-{displayName}";
                var functionsMarkdown = FormatFunctionsMarkdown(discoveredFunctions);

                var skillContent = $@"---
name: {skillName}
description: Autonomous integration skill for {displayName} powered by Composio MCP tools.
status: active
owner: agent360
version: 1.0.0
---

# {displayName.ToUpperInvariant()} Integration Skill (Composio)

This skill provides autonomous capabilities for interacting with {displayName} through the connected Composio MCP driver.

## Available Tool Protocol
- **Transport**: Remote SSE via Composio Hub ({ComposioMcpUrl})
- **MCP Client Key**: `{clientKey}`
- **Discovered Functions Count**: {discoveredFunctions.Count}
- **Last Refreshed At**: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

## Available Functions
{functionsMarkdown}

## Usage Guidelines
1. When a task requires {displayName} actions, inspect available functions from `{clientKey}`.
2. Execute requests with verified parameter schemas matching the discovered endpoints above.
3. Handle return receipts and escalate authorization prompts when required.
";

                var saveSkillBody = new
                {
                    name = skillName,
                    content = skillContent,
                    overwrite = true
                };

                using var saveReq = new HttpRequestMessage(HttpMethod.Put, AgentUri(target, "skills/save"))
                {
                    Content = JsonContent.Create(saveSkillBody)
                };
                saveReq.Headers.Add("X-Agent-Id", targetAgent);
                var saveResp = await SendRoutedAsync(httpClient, routing, targetAgent, saveReq, ct);

                return Results.Ok(new
                {
                    success = saveResp.IsSuccessStatusCode,
                    app_slug = displayName,
                    agent_id = targetAgent,
                    skill_name = skillName,
                    tools_discovered_count = discoveredFunctions.Count,
                    tools = discoveredFunctions,
                    message = $"Schema refreshed for {displayName}: {discoveredFunctions.Count} functions updated in {skillName}"
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("RefreshComposioSchema")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Refresh and hot-update tool schema in SKILL.md from Composio SSE endpoint")
            .AllowAnonymous();

        // 4. Delete / Disconnect MCP client
        routeBuilder.MapDelete(
            "mcp/{clientKey}",
            async (
                string clientKey,
                [FromHeader(Name = "X-Agent-Id")] string? agentIdHeader,
                HttpClient httpClient,
                IFleetInstanceRoutingService routing,
                CancellationToken ct) =>
            {
                var targetAgent = !string.IsNullOrWhiteSpace(agentIdHeader) ? agentIdHeader : "mons";
                if (targetAgent.Contains(':'))
                {
                    targetAgent = targetAgent.Split(':').Last();
                }
                var target = routing.ResolveTarget(targetAgent);
                var requestUri = AgentUri(target, $"mcp/{Uri.EscapeDataString(clientKey)}");

                using var req = new HttpRequestMessage(HttpMethod.Delete, requestUri);
                req.Headers.Add("X-Agent-Id", targetAgent);

                using var resp = await SendRoutedAsync(httpClient, routing, targetAgent, req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return Results.StatusCode((int)resp.StatusCode);
                }

                if (clientKey.StartsWith("composio_"))
                {
                    var appSlug = clientKey.Substring("composio_".Length);
                    var skillName = $"composio-{appSlug}";
                    try
                    {
                        using var skillDelReq = new HttpRequestMessage(HttpMethod.Delete, AgentUri(target, $"skills/{Uri.EscapeDataString(skillName)}"));
                        skillDelReq.Headers.Add("X-Agent-Id", targetAgent);
                        await SendRoutedAsync(httpClient, routing, targetAgent, skillDelReq, ct);
                    }
                    catch
                    {
                        // best effort
                    }
                }

                return Results.Ok(new { message = $"MCP client '{clientKey}' removed from {targetAgent}" });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("DeleteAgentMcpClient")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Disconnect an MCP client from agent")
            .AllowAnonymous();
    }


    private static Uri AgentUri(ResolvedInstanceTarget target, string relative) =>
        new($"{target.BaseUrl.TrimEnd('/')}/api/agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/{relative.TrimStart('/')}");

    private static async Task<HttpResponseMessage> SendRoutedAsync(HttpClient httpClient, IFleetInstanceRoutingService routing, string agentId, HttpRequestMessage request, CancellationToken ct)
    {
        // Re-issue through the fleet router so auth, internal endpoint and public failover apply.
        var target = routing.ResolveTarget(agentId);
        var relative = request.RequestUri!.AbsolutePath.Replace("/api/", "", StringComparison.Ordinal).TrimStart('/') + request.RequestUri.Query;
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var mediaType = request.Content?.Headers.ContentType;
        return await routing.SendAsync(
            target.Instance!,
            request.Method,
            relative,
            body is null ? null : () =>
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = mediaType;
                return content;
            },
            target.TargetAgentProfile,
            TimeSpan.FromSeconds(60),
            ct: ct);
    }

    private static async Task<List<DiscoveredToolInfo>> DiscoverComposioToolsAsync(HttpClient client, string appSlug, string token, CancellationToken ct)
    {
        var results = new List<DiscoveredToolInfo>();

        try
        {
            var serialized = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "tools/call",
                @params = new
                {
                    name = "COMPOSIO_SEARCH_TOOLS",
                    arguments = new { query = appSlug }
                },
                id = 1
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, ComposioMcpUrl)
            {
                Content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Accept", "application/json, text/event-stream");

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return results;

            var rawStream = await resp.Content.ReadAsStringAsync(ct);
            foreach (var line in rawStream.Split((char)10, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("data: "))
                {
                    var dataStr = line.Substring(6).Trim();
                    using var doc = JsonDocument.Parse(dataStr);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("result", out var resultEl) &&
                        resultEl.TryGetProperty("content", out var contentArray) &&
                        contentArray.GetArrayLength() > 0)
                    {
                        var textJson = contentArray[0].GetProperty("text").GetString();
                        if (!string.IsNullOrWhiteSpace(textJson))
                        {
                            using var innerDoc = JsonDocument.Parse(textJson);
                            if (innerDoc.RootElement.TryGetProperty("data", out var dataEl) &&
                                dataEl.TryGetProperty("tool_schemas", out var schemasEl) &&
                                schemasEl.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in schemasEl.EnumerateObject())
                                {
                                    var desc = prop.Value.TryGetProperty("description", out var d) ? d.GetString() : "";
                                    results.Add(new DiscoveredToolInfo(prop.Name, desc ?? ""));
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully
        }

        return results;
    }

    private static string FormatFunctionsMarkdown(List<DiscoveredToolInfo> functions)
    {
        if (functions.Count == 0)
        {
            return "_No dedicated static tool schemas pre-indexed. Functions can be dynamically resolved via `COMPOSIO_SEARCH_TOOLS`._";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var fn in functions)
        {
            sb.AppendLine($"- **`{fn.Name}`**: {fn.Description}");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record ComposioConnectRequest(string AppSlug, string? AgentId, string? Action);
public sealed record ComposioRefreshSchemaRequest(string AppSlug, string? AgentId);
public sealed record DiscoveredToolInfo(string Name, string Description);
