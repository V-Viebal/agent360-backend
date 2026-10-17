using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
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

    private static readonly HashSet<string> ForwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Cache-Control", "ETag", "Last-Modified"
    };

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapMethods(
            "agents/{agentId}/{**path}",
            ["GET", "POST", "PUT", "PATCH", "DELETE"],
            async (string agentId, string path, HttpContext context, IFleetInstanceRoutingService routing, IQwenPawClient client, IMemoryCache cache, CancellationToken ct) =>
            {
                var clean = (path ?? "").Trim('/');
                if (!AllowedPrefixes.Any(p => clean.Equals(p, StringComparison.OrdinalIgnoreCase) || clean.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.NotFound(new { message = $"Route '{clean}' is not exposed for agents" });
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

                var cacheable = HttpMethods.IsGet(context.Request.Method) && clean.Equals("mcp", StringComparison.OrdinalIgnoreCase);
                var cacheKey = $"agent360:proxy:{FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant()}:{clean.ToLowerInvariant()}{context.Request.QueryString}";
                if (cacheable && cache.TryGetValue(cacheKey, out CachedResponse? cached) && cached is not null)
                {
                    context.Response.Headers["X-Agent360-Cache"] = "HIT";
                    return Results.Bytes(cached.Body, cached.ContentType);
                }
                if (!HttpMethods.IsGet(context.Request.Method))
                {
                    cache.Remove($"agent360:proxy:{FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant()}:mcp");
                }

                byte[]? body = null;
                if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                {
                    using var ms = new MemoryStream();
                    await context.Request.Body.CopyToAsync(ms, ct);
                    body = ms.ToArray();
                }
                var contentType = context.Request.ContentType;

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
                        context.Response.Headers["X-Agent360-Cache"] = "MISS";
                        return Results.Bytes(payload, type);
                    }

                    context.Response.StatusCode = (int)response.StatusCode;
                    foreach (var header in response.Content.Headers.Concat(response.Headers))
                    {
                        if (ForwardedResponseHeaders.Contains(header.Key))
                        {
                            context.Response.Headers[header.Key] = header.Value.ToArray();
                        }
                    }
                    await response.Content.CopyToAsync(context.Response.Body, ct);
                    return Results.Empty;
                }
                catch (HttpRequestException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Agent runtime unavailable");
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
