using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.Endpoints;

/// <summary>
/// Allow-listed passthrough to agent-scoped QwenPaw routes (/api/agents/{profile}/...), so every
/// runtime capability is reachable through one consistent Agent360 shape regardless of node.
/// </summary>
public sealed class AgentRuntimeProxyEndpoint : IEndpoint
{
    private static readonly string[] AllowedPrefixes =
    [
        "agent-status",
        "config/channels",
        "config/heartbeat",
        "cron/jobs",
        "mcp",
        "tools",
        "skills",
        "workspace/checkpoints",
        "workspace/running-config",
        "workspace/system-prompt-files",
        "memory",
    ];

    // Read-heavy capability lists: short fresh cache plus a last-good copy served when the runtime is slow or down.
    private static readonly HashSet<string> CachedRoutes = new(StringComparer.OrdinalIgnoreCase) { "mcp", "skills", "tools" };

    private static readonly HashSet<string> ForwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Cache-Control", "ETag", "Last-Modified"
    };

    internal static string CacheKey(string agentId, string route) =>
        $"agent360:proxy:{FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant()}:{route.ToLowerInvariant()}";

    /// <summary>Fetches one cacheable capability list and stores it as fresh + last-good (used by the background warmer).</summary>
    internal static async Task WarmAsync(IFleetInstanceRoutingService routing, IMemoryCache cache, string agentId, ResolvedInstanceTarget target, string route, CancellationToken ct)
    {
        using var response = await routing.SendAsync(
            target.Instance!,
            HttpMethod.Get,
            $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/{route}",
            agentProfileHeader: target.TargetAgentProfile,
            timeout: route.Equals("mcp", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(20),
            ct: ct);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }
        var payload = await response.Content.ReadAsByteArrayAsync(ct);
        var type = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        var key = CacheKey(agentId, route);
        cache.Set(key, new CachedResponse(payload, type), WarmedFreshTtl);
        cache.Set(key + ":last-good", new CachedResponse(payload, type), TimeSpan.FromMinutes(30));
    }

    private static readonly TimeSpan WarmedFreshTtl = TimeSpan.FromSeconds(90);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapMethods(
            "agents/{agentId}/{**path}",
            ["GET", "POST", "PUT", "PATCH", "DELETE"],
            async (string agentId, string path, HttpContext context, IFleetInstanceRoutingService routing, IQwenPawClient client, IMemoryCache cache,
                Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions> security, IHostEnvironment env, CancellationToken ct) =>
            {
                var clean = (path ?? "").Trim('/');
                var isLoopModeRoute = clean.Equals("loop-mode", StringComparison.OrdinalIgnoreCase) || clean.Equals("config/loop-mode", StringComparison.OrdinalIgnoreCase);
                if (security.CurrentValue.ProtectRuntimeProxyWrites && !HttpMethods.IsGet(context.Request.Method) && !isLoopModeRoute
                    && !Agent360.API.Features.Agents.SubAgents.OperatorAuthFilter.IsAuthorized(context, security.CurrentValue, env))
                {
                    return Results.Problem("Operator token required for this action.", statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
                }
                // Disallow reserved static segment collisions (e.g. agents/console/chat must pass to dedicated handler)
                if (agentId.Equals("console", StringComparison.OrdinalIgnoreCase) ||
                    agentId.Equals("models", StringComparison.OrdinalIgnoreCase) ||
                    agentId.Equals("skills", StringComparison.OrdinalIgnoreCase) ||
                    agentId.Equals("chats", StringComparison.OrdinalIgnoreCase) ||
                    agentId.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
                    agentId.Equals("approvals", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(new { message = $"Literal route '{agentId}' is handled by dedicated endpoints" });
                }
                // Dedicated endpoints like channels/responses or chats/{chatId}/stream handle their own traffic
                if (clean.StartsWith("channels/responses", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("chats/", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(new { message = $"Route '{clean}' is handled by dedicated endpoints" });
                }
                if (!AllowedPrefixes.Any(p => clean.Equals(p, StringComparison.OrdinalIgnoreCase) || clean.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.NotFound(new { message = $"Route '{clean}' is not exposed for agents" });
                }

                var (proxyAllowed, proxyReason) = await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.CheckAccessAsync(
                    agentId, context, context.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                    context.RequestServices.GetRequiredService<IConfiguration>(), ct);
                if (!proxyAllowed)
                {
                    return Results.Problem(proxyReason ?? "Agent access denied", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
                }

                var target = routing.ResolveTarget(agentId);
                if (!target.IsQwenPaw)
                {
                    if (clean.Equals("agent-status", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(context.Request.Method))
                    {
                        // Status for non-QwenPaw runtimes comes from fleet health discovery.
                        var agent = (await client.GetAgentsAsync(ct)).FirstOrDefault(a => a.Slug.Equals(target.AgentId, StringComparison.OrdinalIgnoreCase));
                        return agent is null
                            ? Results.NotFound(new { message = $"Agent '{agentId}' not found in fleet" })
                            : Results.Ok(new
                            {
                                status = !agent.Enabled ? "disabled" : agent.Status == "active" ? "idle" : agent.Status,
                                running_task_count = 0,
                                last_run_at = (string?)null,
                                last_finish_at = (string?)null,
                                source = "fleet-health"
                            });
                    }
                    return Results.NotFound(new { message = $"Agent '{agentId}' is not hosted on a QwenPaw runtime" });
                }

                // QwenPaw v2.0.x has no checkpoint router; report the capability instead of a raw 404.
                if (clean.StartsWith("workspace/checkpoints/status", StringComparison.OrdinalIgnoreCase))
                {
                    using var probe = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/{clean}", agentProfileHeader: target.TargetAgentProfile, ct: ct);
                    if (probe.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return Results.Ok(new { supported = false, auto_enabled = false, has_checkpoints = false });
                    }
                    context.Response.StatusCode = (int)probe.StatusCode;
                    context.Response.ContentType = probe.Content.Headers.ContentType?.ToString() ?? "application/json";
                    await probe.Content.CopyToAsync(context.Response.Body, ct);
                    return Results.Empty;
                }

                var cacheable = HttpMethods.IsGet(context.Request.Method) && CachedRoutes.Contains(clean);
                var agentKey = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
                var cacheKey = CacheKey(agentId, clean) + context.Request.QueryString;
                var lastGoodKey = cacheKey + ":last-good";
                if (cacheable && cache.TryGetValue(cacheKey, out CachedResponse? cached) && cached is not null)
                {
                    context.Response.Headers["X-Agent360-Cache"] = "HIT";
                    return Results.Bytes(SecretRedaction.RedactJson(cached.Body), cached.ContentType);
                }
                if (!HttpMethods.IsGet(context.Request.Method))
                {
                    // Writes invalidate the fresh caches of the touched capability family.
                    foreach (var route in CachedRoutes)
                    {
                        if (clean.StartsWith(route, StringComparison.OrdinalIgnoreCase))
                        {
                            cache.Remove($"agent360:proxy:{agentKey}:{route}");
                        }
                    }
                    if (clean.StartsWith("skills", StringComparison.OrdinalIgnoreCase))
                    {
                        cache.Remove($"agent360:skills:{agentKey}");
                    }
                }

                byte[]? body = null;
                if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                {
                    using var ms = new MemoryStream();
                    await context.Request.Body.CopyToAsync(ms, ct);
                    body = ms.ToArray();
                }
                var contentType = context.Request.ContentType;
                // The UI saves by merging a (redacted) GET back into the PUT body: put the live secrets back
                // at the same JSON paths so a save never overwrites a credential with the mask.
                if (body is not null && contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                    && System.Text.Encoding.UTF8.GetString(body).Contains(SecretRedaction.Mask, StringComparison.Ordinal))
                {
                    string? live = null;
                    try
                    {
                        using var current = await routing.SendAsync(target.Instance!, HttpMethod.Get,
                            $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/{clean}", agentProfileHeader: target.TargetAgentProfile, ct: ct);
                        if (current.IsSuccessStatusCode) live = await current.Content.ReadAsStringAsync(ct);
                    }
                    catch (HttpRequestException) { }
                    body = System.Text.Encoding.UTF8.GetBytes(SecretRedaction.RestoreMaskedValues(System.Text.Encoding.UTF8.GetString(body), live));
                }

                try
                {
                    using var response = await routing.SendAsync(
                        target.Instance!,
                        new HttpMethod(context.Request.Method),
                        $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/{clean}{context.Request.QueryString}",
                        body is null ? null : () =>
                        {
                            var content = new ByteArrayContent(body);
                            if (!string.IsNullOrWhiteSpace(contentType))
                            {
                                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                            }
                            return content;
                        },
                        target.TargetAgentProfile,
                        clean.StartsWith("mcp", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(20),
                        ct: ct);

                    if (cacheable && response.IsSuccessStatusCode)
                    {
                        var payload = await response.Content.ReadAsByteArrayAsync(ct);
                        var type = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                        cache.Set(cacheKey, new CachedResponse(payload, type), TimeSpan.FromSeconds(30));
                        cache.Set(lastGoodKey, new CachedResponse(payload, type), TimeSpan.FromMinutes(30));
                        context.Response.Headers["X-Agent360-Cache"] = "MISS";
                        return Results.Bytes(SecretRedaction.RedactJson(payload), type);
                    }

                    if (cacheable && (int)response.StatusCode >= 500 && cache.TryGetValue(lastGoodKey, out CachedResponse? staleOnError) && staleOnError is not null)
                    {
                        context.Response.Headers["X-Agent360-Cache"] = "STALE";
                        return Results.Bytes(SecretRedaction.RedactJson(staleOnError.Body), staleOnError.ContentType);
                    }

                    context.Response.StatusCode = (int)response.StatusCode;
                    foreach (var header in response.Content.Headers.Concat(response.Headers))
                    {
                        if (ForwardedResponseHeaders.Contains(header.Key))
                        {
                            context.Response.Headers[header.Key] = header.Value.ToArray();
                        }
                    }
                    var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                    {
                        // Agent configs carry channel bot tokens and MCP env secrets: never return them raw.
                        var json = SecretRedaction.RedactJson(await response.Content.ReadAsByteArrayAsync(ct));
                        context.Response.Headers.ContentLength = json.Length;
                        await context.Response.Body.WriteAsync(json, ct);
                        return Results.Empty;
                    }
                    await response.Content.CopyToAsync(context.Response.Body, ct);
                    return Results.Empty;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    if (cacheable && cache.TryGetValue(lastGoodKey, out CachedResponse? stale) && stale is not null)
                    {
                        context.Response.Headers["X-Agent360-Cache"] = "STALE";
                        return Results.Bytes(SecretRedaction.RedactJson(stale.Body), stale.ContentType);
                    }
                    // Do not echo transport errors (internal hosts/ports) to anonymous callers.
                    context.Response.Headers.RetryAfter = "5";
                    return Results.Problem("The agent runtime did not answer. Retry shortly.", statusCode: StatusCodes.Status502BadGateway, title: "Agent runtime unavailable");
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("AgentRuntimeProxy")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Agent-scoped passthrough to the agent's QwenPaw runtime (allow-listed routes)")
            .AllowAnonymous();
    }
}

internal sealed record CachedResponse(byte[] Body, string ContentType);
