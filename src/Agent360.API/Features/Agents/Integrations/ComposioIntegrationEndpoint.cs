using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Integrations;

public sealed class ComposioIntegrationEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. Get MCP clients for an agent (proxy to QwenPaw /api/mcp with X-Agent-Id)
        routeBuilder.MapGet(
            "mcp",
            async (
                [FromHeader(Name = "X-Agent-Id")] string? agentIdHeader,
                [FromQuery] string? agentIdQuery,
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken ct) =>
            {
                var targetAgent = !string.IsNullOrWhiteSpace(agentIdHeader) 
                    ? agentIdHeader 
                    : (!string.IsNullOrWhiteSpace(agentIdQuery) ? agentIdQuery : "mons");
                
                if (targetAgent.Contains(':'))
                {
                    targetAgent = targetAgent.Split(':').Last();
                }

                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, "api/mcp");

                using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
                req.Headers.Add("X-Agent-Id", targetAgent);

                using var resp = await httpClient.SendAsync(req, ct);
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

        // 2. Connect Composio Tool / App: Registers MCP driver card and materializes dedicated Skill in agent workspace
        routeBuilder.MapPost(
            "integrations/composio/connect",
            async (
                [FromBody] ComposioConnectRequest request,
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
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

                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");

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
                        url = "https://connect.composio.dev/mcp",
                        headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = "Bearer ck_hRHd3TK_vE4wwTBlUaX3"
                        },
                        enabled = true
                    }
                };

                using var mcpReq = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/mcp"))
                {
                    Content = JsonContent.Create(mcpCreateBody)
                };
                mcpReq.Headers.Add("X-Agent-Id", targetAgent);

                var mcpResp = await httpClient.SendAsync(mcpReq, ct);
                if (!mcpResp.IsSuccessStatusCode && mcpResp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    using var toggleReq = new HttpRequestMessage(HttpMethod.Patch, new Uri(baseUri, $"api/mcp/toggle/{Uri.EscapeDataString(clientKey)}"));
                    toggleReq.Headers.Add("X-Agent-Id", targetAgent);
                    await httpClient.SendAsync(toggleReq, ct);
                }

                // 2b. Materialize skill in agent workspace via QwenPaw POST /api/skills (create_skill)
                var skillName = $"composio-{displayName}";
                var skillContent = $"---\nname: {skillName}\ndescription: Autonomous integration skill for {displayName} powered by Composio MCP tools.\nstatus: active\nowner: agent360\nversion: 1.0.0\n---\n\n# {displayName.ToUpperInvariant()} Integration Skill (Composio)\n\nThis skill provides autonomous capabilities for interacting with {displayName} through the connected Composio MCP driver.\n\n## Available Tool Protocol\n- **Transport**: Remote SSE via Composio Hub\n- **MCP Client Key**: `{clientKey}`\n- **Capabilities**: Querying, mutating, and event-handling for {displayName}.\n\n## Usage Guidelines\n1. When a task requires {displayName} actions, inspect available functions from `{clientKey}`.\n2. Execute requests with verified parameter schemas.\n3. Handle return receipts and escalate authorization prompts when required.\n";

                var createSkillBody = new
                {
                    name = skillName,
                    content = skillContent,
                    enable = true
                };

                using var skillReq = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/skills"))
                {
                    Content = JsonContent.Create(createSkillBody)
                };
                skillReq.Headers.Add("X-Agent-Id", targetAgent);

                var skillResp = await httpClient.SendAsync(skillReq, ct);
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
                    using var saveReq = new HttpRequestMessage(HttpMethod.Put, new Uri(baseUri, "api/skills/save"))
                    {
                        Content = JsonContent.Create(saveSkillBody)
                    };
                    saveReq.Headers.Add("X-Agent-Id", targetAgent);
                    var saveResp = await httpClient.SendAsync(saveReq, ct);
                    skillCreated = saveResp.IsSuccessStatusCode;
                }

                // Ensure skill is enabled
                try
                {
                    using var enableReq = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"api/skills/{Uri.EscapeDataString(skillName)}/enable"));
                    enableReq.Headers.Add("X-Agent-Id", targetAgent);
                    await httpClient.SendAsync(enableReq, ct);
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
                    message = $"Successfully connected {displayName} to {targetAgent} and provisioned skill {skillName}"
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ConnectComposioApp")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Connect and apply Composio application and provision matching skill for agent")
            .AllowAnonymous();

        // 3. Delete / Disconnect MCP client
        routeBuilder.MapDelete(
            "mcp/{clientKey}",
            async (
                string clientKey,
                [FromHeader(Name = "X-Agent-Id")] string? agentIdHeader,
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken ct) =>
            {
                var targetAgent = !string.IsNullOrWhiteSpace(agentIdHeader) ? agentIdHeader : "mons";
                if (targetAgent.Contains(':'))
                {
                    targetAgent = targetAgent.Split(':').Last();
                }

                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, $"api/mcp/{Uri.EscapeDataString(clientKey)}");

                using var req = new HttpRequestMessage(HttpMethod.Delete, requestUri);
                req.Headers.Add("X-Agent-Id", targetAgent);

                using var resp = await httpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return Results.StatusCode((int)resp.StatusCode);
                }

                // If it's a Composio app, also clean up the associated skill
                if (clientKey.StartsWith("composio_"))
                {
                    var appSlug = clientKey.Substring("composio_".Length);
                    var skillName = $"composio-{appSlug}";
                    try
                    {
                        using var skillDelReq = new HttpRequestMessage(HttpMethod.Delete, new Uri(baseUri, $"api/skills/{Uri.EscapeDataString(skillName)}"));
                        skillDelReq.Headers.Add("X-Agent-Id", targetAgent);
                        await httpClient.SendAsync(skillDelReq, ct);
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
}

public sealed record ComposioConnectRequest(string AppSlug, string? AgentId, string? Action);
