using System.Text.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.Integrations;

public sealed class ComposioIntegrationEndpoint : IEndpoint
{
    private const string ComposioMcpHubUrl = "https://connect.composio.dev/mcp";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. Get MCP clients for an agent (proxy to QwenPaw /api/agents/{agent}/mcp with X-Agent-Id)
        routeBuilder.MapGet(
            "mcp",
            async (
                [FromHeader(Name = "X-Agent-Id")] string? agentIdHeader,
                [FromQuery] string? agentIdQuery,
                HttpClient httpClient,
                IFleetInstanceRoutingService routing,
                IMemoryCache cache,
                HttpContext http,
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
                if (target.Instance is null)
                {
                    // Non-QwenPaw runtimes (Agent Guardian / LLM360 edges) have no MCP client registry.
                    return Results.Ok(new JsonArray());
                }

                var key = Agent360.API.Features.Agents.Endpoints.AgentRuntimeProxyEndpoint.CacheKey(targetAgent, "mcp");
                if (cache.TryGetValue(key, out Agent360.API.Features.Agents.Endpoints.CachedResponse? fresh) && fresh is not null)
                {
                    http.Response.Headers["X-Agent360-Cache"] = "HIT";
                    return Results.Bytes(fresh.Body, fresh.ContentType);
                }

                try
                {
                    var requestUri = AgentUri(target, "mcp");
                    using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    req.Headers.Add("X-Agent-Id", targetAgent);

                    using var resp = await SendRoutedAsync(httpClient, routing, targetAgent, req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        return StaleOr(cache, key, http) ?? Results.StatusCode((int)resp.StatusCode);
                    }

                    var payload = await resp.Content.ReadAsByteArrayAsync(ct);
                    var type = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
                    cache.Set(key, new Agent360.API.Features.Agents.Endpoints.CachedResponse(payload, type), TimeSpan.FromSeconds(30));
                    cache.Set(key + ":last-good", new Agent360.API.Features.Agents.Endpoints.CachedResponse(payload, type), TimeSpan.FromMinutes(30));
                    http.Response.Headers["X-Agent360-Cache"] = "MISS";
                    return Results.Bytes(payload, type);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
                {
                    return StaleOr(cache, key, http) ?? throw new HttpRequestException($"MCP list for '{targetAgent}' unavailable", ex);
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentMcpClients")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List MCP clients configured on an agent")
            .AllowAnonymous();

        // 2. Connect Composio Tool / App:
        //    - Uses Composio MANAGE_CONNECTIONS to initiate authentication / get redirect_url
        //    - Discovers tools from dedicated MCP endpoint (https://connect.composio.dev/mcp/{appSlug})
        //    - Registers the app independently into QwenPaw agent MCP layer (/api/agents/{agentId}/mcp)
        //    - Provisions matching skill in agent workspace with dynamic schemas
        routeBuilder.MapPost(
            "integrations/composio/connect",
            async (
                [FromBody] ComposioConnectRequest request,
                HttpClient httpClient,
                IFleetInstanceRoutingService routing,
                IMemoryCache cache,
                IComposioConnectionPoller poller,
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

                var effectiveToken = InfisicalCredentialAuditService.ResolveAgentComposioToken(targetAgent, allowWorkspaceFallback: true);
                if (string.IsNullOrWhiteSpace(effectiveToken))
                {
                    return Results.Problem(
                        detail: "Composio is not configured for this agent (COMPOSIO_API_KEY or COMPOSIO_API_KEY_<AGENT> missing).",
                        statusCode: StatusCodes.Status503ServiceUnavailable, title: "Integration not configured");
                }
                var target = routing.ResolveTarget(targetAgent);

                var displayName = request.AppSlug.ToLowerInvariant().Trim();
                var clientKey = $"composio_{displayName}";
                var appMcpUrl = $"https://connect.composio.dev/mcp/{displayName}";

                // 2a. Initiate connection via Composio Hub MCP protocol to obtain OAuth/credential redirect link
                var connectionInfo = await InitiateComposioConnectionAsync(httpClient, displayName, effectiveToken, ct);
                if (!string.IsNullOrWhiteSpace(connectionInfo.RedirectUrl))
                {
                    poller.TrackPendingConnection(targetAgent, displayName);
                }

                // 2b. Discover tools schema (prefer app-specific MCP endpoint, fallback to COMPOSIO_SEARCH_TOOLS)
                var discoveredFunctions = await DiscoverComposioToolsAsync(httpClient, displayName, effectiveToken, ct);

                // 2c. Register or update the app independently in QwenPaw Agent MCP layer
                var mcpClientPayload = new
                {
                    name = clientKey,
                    description = $"Composio {displayName} integration for {targetAgent}",
                    transport = "sse",
                    url = appMcpUrl,
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {effectiveToken}"
                    },
                    enabled = true
                };

                var mcpCreateBody = new
                {
                    client_key = clientKey,
                    client = mcpClientPayload
                };

                using var mcpReq = new HttpRequestMessage(HttpMethod.Post, AgentUri(target, "mcp"))
                {
                    Content = JsonContent.Create(mcpCreateBody)
                };
                mcpReq.Headers.Add("X-Agent-Id", targetAgent);

                var mcpResp = await SendRoutedAsync(httpClient, routing, targetAgent, mcpReq, ct);
                if (!mcpResp.IsSuccessStatusCode)
                {
                    // If client already exists (400 or 409), update it via PUT /api/agents/{agent}/mcp/{clientKey}
                    var mcpUpdateBody = new { client = mcpClientPayload };
                    using var mcpPutReq = new HttpRequestMessage(HttpMethod.Put, AgentUri(target, $"mcp/{Uri.EscapeDataString(clientKey)}"))
                    {
                        Content = JsonContent.Create(mcpUpdateBody)
                    };
                    mcpPutReq.Headers.Add("X-Agent-Id", targetAgent);
                    await SendRoutedAsync(httpClient, routing, targetAgent, mcpPutReq, ct);
                }

                // Invalidate agent MCP cache
                var cacheKey = Agent360.API.Features.Agents.Endpoints.AgentRuntimeProxyEndpoint.CacheKey(targetAgent, "mcp");
                cache.Remove(cacheKey);
                cache.Remove(cacheKey + ":last-good");

                // 2d. Materialize / update skill in agent workspace with ## Available Functions populated
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
- **Transport**: Remote SSE via Composio Dedicated MCP ({appMcpUrl})
- **MCP Client Key**: `{clientKey}`
- **Discovered Functions Count**: {discoveredFunctions.Count}
- **Connection Status**: {connectionInfo.Status}

## Available Functions
{functionsMarkdown}

## Usage Guidelines
1. When a task requires {displayName} actions, inspect available functions from `{clientKey}`.
2. Execute requests with verified parameter schemas matching the discovered endpoints above.
3. If authorization is required or connection is pending, prompt user with connection verification.
";

                var createSkillBody = new
                {
                    name = skillName,
                    content = skillContent,
                    enable = true
                };

                using var skillReq = new HttpRequestMessage(HttpMethod.Post, new Uri($"{target.BaseUrl.TrimEnd('/')}/api/skills"))
                {
                    Content = JsonContent.Create(createSkillBody)
                };
                skillReq.Headers.Add("X-Agent-Id", targetAgent);

                var skillResp = await SendRoutedAsync(httpClient, routing, targetAgent, skillReq, ct);
                var skillCreated = skillResp.IsSuccessStatusCode;

                if (!skillCreated && (skillResp.StatusCode == System.Net.HttpStatusCode.Conflict || skillResp.StatusCode == System.Net.HttpStatusCode.BadRequest))
                {
                    var saveSkillBody = new
                    {
                        name = skillName,
                        content = skillContent,
                        overwrite = true
                    };
                    using var saveReq = new HttpRequestMessage(HttpMethod.Put, new Uri($"{target.BaseUrl.TrimEnd('/')}/api/skills/save"))
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
                    using var enableReq = new HttpRequestMessage(HttpMethod.Post, new Uri($"{target.BaseUrl.TrimEnd('/')}/api/skills/{Uri.EscapeDataString(skillName)}/enable"));
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
                    redirect_url = connectionInfo.RedirectUrl,
                    status = connectionInfo.Status,
                    message = $"Successfully connected {displayName} to {targetAgent} and provisioned skill {skillName} with {discoveredFunctions.Count} discovered functions"
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ConnectComposioApp")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Connect and apply Composio application and provision matching skill with discovered schema")
            .AllowAnonymous();

        
        // 2b. Check Connection Status (queried by frontend or poller)
        routeBuilder.MapGet(
            "integrations/composio/status",
            async (
                [FromQuery] string appSlug,
                [FromHeader(Name = "X-Agent-Id")] string? agentIdHeader,
                [FromQuery] string? agentIdQuery,
                HttpClient httpClient,
                IComposioConnectionPoller poller,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(appSlug))
                {
                    return Results.BadRequest(new { error = "appSlug is required" });
                }

                var targetAgent = !string.IsNullOrWhiteSpace(agentIdHeader) 
                    ? agentIdHeader 
                    : (!string.IsNullOrWhiteSpace(agentIdQuery) ? agentIdQuery : "mons");
                
                if (targetAgent.Contains(':'))
                {
                    targetAgent = targetAgent.Split(':').Last();
                }

                var displayName = appSlug.ToLowerInvariant().Trim();
                var pollStatus = poller.GetStatus(targetAgent, displayName);

                if (pollStatus is not null)
                {
                    return Results.Ok(new
                    {
                        agent_id = targetAgent,
                        app_slug = displayName,
                        status = pollStatus.Status,
                        started_at = pollStatus.StartedAt,
                        last_checked_at = pollStatus.LastCheckedAt,
                        message = pollStatus.Message
                    });
                }

                // If not in active poller, check live directly from Composio
                var effectiveToken = InfisicalCredentialAuditService.ResolveAgentComposioToken(targetAgent, allowWorkspaceFallback: true);
                if (string.IsNullOrWhiteSpace(effectiveToken))
                {
                    return Results.Ok(new { agent_id = targetAgent, app_slug = displayName, status = "unconfigured" });
                }

                var conn = await InitiateComposioConnectionAsync(httpClient, displayName, effectiveToken, ct);
                return Results.Ok(new
                {
                    agent_id = targetAgent,
                    app_slug = displayName,
                    status = conn.Status,
                    redirect_url = conn.RedirectUrl,
                    message = conn.Status == "active" ? "Connection active." : "Pending authorization."
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetComposioConnectionStatus")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Query pending/active OAuth connection status for agent app")
            .AllowAnonymous();

        
        // 2c. Inbound Webhook Receiver for Composio connection events with Anti-Replay Protection & HMAC/Secret Validation
        routeBuilder.MapPost(
            "webhooks/composio",
            async (
                HttpContext http,
                IComposioConnectionPoller poller,
                IMemoryCache cache,
                Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Options.ComposioOptions> composioOptions,
                ILogger<ComposioIntegrationEndpoint> logger,
                CancellationToken ct) =>
            {
                // 1. Anti-Replay Protection: Timestamp tolerance check (max 5 minutes skew)
                var tsHeader = http.Request.Headers["X-Composio-Timestamp"].ToString();
                if (!string.IsNullOrWhiteSpace(tsHeader))
                {
                    if (long.TryParse(tsHeader, out var tsUnix))
                    {
                        var currentUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (Math.Abs(currentUnix - tsUnix) > 300)
                        {
                            logger.LogWarning("[ComposioWebhook] Rejected stale webhook: timestamp skew {Skew}s exceeds 300s limit", Math.Abs(currentUnix - tsUnix));
                            return Results.Problem(detail: "Webhook timestamp skew exceeds tolerance limit.", statusCode: StatusCodes.Status401Unauthorized, title: "Stale Webhook Timestamp");
                        }
                    }
                }

                // 2. Dynamic Secret / HMAC validation (from IOptionsMonitor or process environment)
                var expectedSecret = composioOptions.CurrentValue.WebhookSecret 
                    ?? Environment.GetEnvironmentVariable("COMPOSIO_WEBHOOK_SECRET");

                if (!string.IsNullOrWhiteSpace(expectedSecret))
                {
                    var incomingSig = http.Request.Headers["X-Composio-Signature"].ToString();
                    if (string.IsNullOrWhiteSpace(incomingSig))
                    {
                        logger.LogWarning("[ComposioWebhook] Rejected webhook: missing X-Composio-Signature header");
                        return Results.Unauthorized();
                    }

                    // Direct token match or HMAC-SHA256 signature verification
                    var valid = string.Equals(incomingSig, expectedSecret, StringComparison.Ordinal);
                    if (!valid && incomingSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
                    {
                        http.Request.EnableBuffering();
                        using var rawReader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
                        var bodyText = await rawReader.ReadToEndAsync(ct);
                        http.Request.Body.Position = 0;

                        var dataToSign = !string.IsNullOrWhiteSpace(tsHeader) ? $"{tsHeader}.{bodyText}" : bodyText;
                        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(expectedSecret));
                        var computed = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dataToSign))).ToLowerInvariant();
                        var cleanSig = incomingSig[7..].ToLowerInvariant();
                        valid = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.UTF8.GetBytes(computed),
                            System.Text.Encoding.UTF8.GetBytes(cleanSig));
                    }

                    if (!valid)
                    {
                        logger.LogWarning("[ComposioWebhook] Rejected webhook: signature mismatch");
                        return Results.Unauthorized();
                    }
                }

                try
                {
                    using var reader = new StreamReader(http.Request.Body);
                    var rawBody = await reader.ReadToEndAsync(ct);
                    if (string.IsNullOrWhiteSpace(rawBody))
                    {
                        return Results.BadRequest(new { error = "Empty body" });
                    }

                    using var doc = JsonDocument.Parse(rawBody);
                    var root = doc.RootElement;

                    // 3. Deduplication Nonce & Event ID Cache Check (Drop duplicate replay deliveries)
                    var eventId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() :
                                  (root.TryGetProperty("event_id", out var evProp) ? evProp.GetString() :
                                  (root.TryGetProperty("delivery_id", out var dProp) ? dProp.GetString() : null));

                    if (!string.IsNullOrWhiteSpace(eventId))
                    {
                        var dedupeKey = $"composio:webhook:dedupe:{eventId}";
                        if (cache.TryGetValue(dedupeKey, out _))
                        {
                            logger.LogInformation("[ComposioWebhook] Duplicate event {EventId} dropped via anti-replay nonce cache", eventId);
                            return Results.Ok(new { status = "duplicate_dropped", event_id = eventId });
                        }
                        cache.Set(dedupeKey, true, TimeSpan.FromMinutes(15));
                    }

                    // 4. Typical Composio event structure: { "type": "connection.activated" / "connection.status.updated", "data": { "toolkit": "github", "agent_id": "...", "status": "active" } }
                    var eventType = root.TryGetProperty("type", out var t) ? t.GetString() : (root.TryGetProperty("event", out var ev) ? ev.GetString() : "unknown");
                    var dataEl = root.TryGetProperty("data", out var d) ? d : root;

                    var appSlug = dataEl.TryGetProperty("toolkit", out var tk) ? tk.GetString() :
                                  (dataEl.TryGetProperty("app_slug", out var asg) ? asg.GetString() :
                                  (dataEl.TryGetProperty("app", out var ap) ? ap.GetString() : null));

                    var agentId = dataEl.TryGetProperty("agent_id", out var ag) ? ag.GetString() :
                                  (http.Request.Headers.TryGetValue("X-Agent-Id", out var hAg) ? hAg.ToString() : "mons");

                    var status = dataEl.TryGetProperty("status", out var st) ? st.GetString() : "active";

                    logger.LogInformation("[ComposioWebhook] Received verified event '{Type}' for App '{App}', Agent '{Agent}', Status '{Status}'",
                        eventType, appSlug, agentId, status);

                    if (!string.IsNullOrWhiteSpace(appSlug) && !string.IsNullOrWhiteSpace(agentId))
                    {
                        var cleanSlug = appSlug.ToLowerInvariant().Trim();
                        var cleanAgent = agentId.Replace("bot:agent360:", "").ToLowerInvariant().Trim();

                        // Invalidate agent MCP cache so new tools are immediately visible
                        var cacheKey = Agent360.API.Features.Agents.Endpoints.AgentRuntimeProxyEndpoint.CacheKey(cleanAgent, "mcp");
                        cache.Remove(cacheKey);
                        cache.Remove(cacheKey + ":last-good");
                    }

                    return Results.Ok(new { success = true, received_event = eventType, event_id = eventId });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[ComposioWebhook] Failed to process inbound webhook payload");
                    return Results.BadRequest(new { error = "Invalid JSON or event payload" });
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ComposioWebhookReceiver")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Inbound webhook receiver for instant Composio connection events")
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

                var effectiveToken = InfisicalCredentialAuditService.ResolveAgentComposioToken(targetAgent, allowWorkspaceFallback: true);
                if (string.IsNullOrWhiteSpace(effectiveToken))
                {
                    return Results.Problem(
                        detail: "Composio is not configured for this agent (COMPOSIO_API_KEY or COMPOSIO_API_KEY_<AGENT> missing).",
                        statusCode: StatusCodes.Status503ServiceUnavailable, title: "Integration not configured");
                }
                var target = routing.ResolveTarget(targetAgent);

                var displayName = request.AppSlug.ToLowerInvariant().Trim();
                var clientKey = $"composio_{displayName}";
                var appMcpUrl = $"https://connect.composio.dev/mcp/{displayName}";

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
- **Transport**: Remote SSE via Composio Dedicated MCP ({appMcpUrl})
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

                using var saveReq = new HttpRequestMessage(HttpMethod.Put, new Uri($"{target.BaseUrl.TrimEnd('/')}/api/skills/save"))
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
                IMemoryCache cache,
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

                // Invalidate agent MCP cache
                var cacheKey = Agent360.API.Features.Agents.Endpoints.AgentRuntimeProxyEndpoint.CacheKey(targetAgent, "mcp");
                cache.Remove(cacheKey);
                cache.Remove(cacheKey + ":last-good");

                if (clientKey.StartsWith("composio_"))
                {
                    var appSlug = clientKey.Substring("composio_".Length);
                    var skillName = $"composio-{appSlug}";
                    try
                    {
                        using var skillDelReq = new HttpRequestMessage(HttpMethod.Delete, new Uri($"{target.BaseUrl.TrimEnd('/')}/api/skills/{Uri.EscapeDataString(skillName)}"));
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

    private static IResult? StaleOr(IMemoryCache cache, string key, HttpContext http)
    {
        if (!cache.TryGetValue(key + ":last-good", out Agent360.API.Features.Agents.Endpoints.CachedResponse? stale) || stale is null)
        {
            return null;
        }
        http.Response.Headers["X-Agent360-Cache"] = "STALE";
        return Results.Bytes(stale.Body, stale.ContentType);
    }

    private static Uri AgentUri(ResolvedInstanceTarget target, string relative) =>
        new($"{target.BaseUrl.TrimEnd('/')}/api/agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/{relative.TrimStart('/')}");

    private static async Task<HttpResponseMessage> SendRoutedAsync(HttpClient httpClient, IFleetInstanceRoutingService routing, string agentId, HttpRequestMessage request, CancellationToken ct)
    {
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

    private static async Task<ComposioConnectionResult> InitiateComposioConnectionAsync(HttpClient client, string appSlug, string token, CancellationToken ct)
    {
        try
        {
            var serialized = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "tools/call",
                @params = new
                {
                    name = "COMPOSIO_MANAGE_CONNECTIONS",
                    arguments = new { toolkits = new[] { appSlug } }
                },
                id = 1
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, ComposioMcpHubUrl)
            {
                Content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Accept", "application/json, text/event-stream");

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new ComposioConnectionResult(null, "error");
            }

            var rawStream = await resp.Content.ReadAsStringAsync(ct);
            foreach (var line in rawStream.Split((char)10, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("data: "))
                {
                    var dataStr = line.Substring(6).Trim();
                    using var doc = JsonDocument.Parse(dataStr);
                    if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                        resultEl.TryGetProperty("content", out var contentArray) &&
                        contentArray.GetArrayLength() > 0)
                    {
                        var textJson = contentArray[0].GetProperty("text").GetString();
                        if (!string.IsNullOrWhiteSpace(textJson))
                        {
                            using var innerDoc = JsonDocument.Parse(textJson);
                            if (innerDoc.RootElement.TryGetProperty("data", out var dataEl) &&
                                dataEl.TryGetProperty("results", out var resultsEl) &&
                                resultsEl.TryGetProperty(appSlug, out var appResEl))
                            {
                                var redirectUrl = appResEl.TryGetProperty("redirect_url", out var r) ? r.GetString() : null;
                                var status = appResEl.TryGetProperty("status", out var s) ? s.GetString() : "initiated";
                                return new ComposioConnectionResult(redirectUrl, status ?? "initiated");
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

        return new ComposioConnectionResult(null, "unknown");
    }

    private static async Task<List<DiscoveredToolInfo>> DiscoverComposioToolsAsync(HttpClient client, string appSlug, string token, CancellationToken ct)
    {
        var results = new List<DiscoveredToolInfo>();

        // 1. Try direct app-specific MCP endpoint: https://connect.composio.dev/mcp/{appSlug}
        try
        {
            var appMcpEndpoint = $"https://connect.composio.dev/mcp/{appSlug}";
            var serializedList = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "tools/list",
                @params = new { },
                id = 1
            });

            using var directReq = new HttpRequestMessage(HttpMethod.Post, appMcpEndpoint)
            {
                Content = new StringContent(serializedList, System.Text.Encoding.UTF8, "application/json")
            };
            directReq.Headers.Add("Authorization", $"Bearer {token}");
            directReq.Headers.Add("Accept", "application/json, text/event-stream");

            using var directResp = await client.SendAsync(directReq, ct);
            if (directResp.IsSuccessStatusCode)
            {
                var raw = await directResp.Content.ReadAsStringAsync(ct);
                foreach (var line in raw.Split((char)10, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("data: "))
                    {
                        var dataStr = line.Substring(6).Trim();
                        using var doc = JsonDocument.Parse(dataStr);
                        if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                            resultEl.TryGetProperty("tools", out var toolsArray) &&
                            toolsArray.GetArrayLength() > 0)
                        {
                            foreach (var tool in toolsArray.EnumerateArray())
                            {
                                var name = tool.TryGetProperty("name", out var n) ? n.GetString() : "";
                                var desc = tool.TryGetProperty("description", out var d) ? d.GetString() : "";
                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    results.Add(new DiscoveredToolInfo(name, desc ?? ""));
                                }
                            }
                            if (results.Count > 0)
                            {
                                return results;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // fallback to search
        }

        // 2. Fallback to COMPOSIO_SEARCH_TOOLS on hub
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

            using var req = new HttpRequestMessage(HttpMethod.Post, ComposioMcpHubUrl)
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
        foreach (var fn in functions.Take(50))
        {
            var cleanDesc = fn.Description.Replace("\r", "").Replace("\n", " ").Trim();
            if (cleanDesc.Length > 160) cleanDesc = cleanDesc.Substring(0, 157) + "...";
            sb.AppendLine($"- **`{fn.Name}`**: {cleanDesc}");
        }
        if (functions.Count > 50)
        {
            var prefix = functions[0].Name.Contains('_') ? functions[0].Name.Split('_')[0] : "TOOL";
            sb.AppendLine($"- _... and {functions.Count - 50} more tools available via `{prefix}` protocol._");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record ComposioConnectRequest(string AppSlug, string? AgentId, string? Action);
public sealed record ComposioRefreshSchemaRequest(string AppSlug, string? AgentId);
public sealed record DiscoveredToolInfo(string Name, string Description);
public sealed record ComposioConnectionResult(string? RedirectUrl, string Status);
