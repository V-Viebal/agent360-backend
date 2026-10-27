using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Features.Agents.SubAgents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.Webhooks;

public sealed class AgentWebhookEndpoints : IEndpoint
{
    private const string SignatureHeader = "X-Agent360-Signature";
    private const string TimestampHeader = "X-Agent360-Timestamp";
    private const string AltSignatureHeader = "X-Webhook-Signature";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. GET /api/v1/agents/{agentId}/webhooks/config
        routeBuilder.MapGet("agents/{agentId}/webhooks/config", async (
            string agentId,
            HttpContext httpContext,
            AgentWebhookStore store,
            IFleetInstanceRoutingService routing,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var dto = await BuildConfigDtoAsync(agentId, httpContext, store, routing, ct);
            return Results.Ok(dto);
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("GetAgentWebhookConfig")
        .WithTags(EndpointTags.Agents)
        .WithSummary("Get inbound/outbound webhook settings, Matrix bridge state, and QwenPaw runtime status for an agent")
        .AllowAnonymous();

        // 2. PUT /api/v1/agents/{agentId}/webhooks/config
        routeBuilder.MapPut("agents/{agentId}/webhooks/config", async (
            string agentId,
            [FromBody] UpdateAgentWebhookConfigRequest request,
            HttpContext httpContext,
            AgentWebhookStore store,
            IFleetInstanceRoutingService routing,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var cleanAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
            await store.SaveStateAsync(cleanAgent, state =>
            {
                if (request.Enabled.HasValue) state.Enabled = request.Enabled.Value;
                if (request.RequireHmac.HasValue) state.RequireHmac = request.RequireHmac.Value;
                if (request.AllowedEvents is { Length: > 0 }) state.AllowedEvents = request.AllowedEvents;
                if (!string.IsNullOrWhiteSpace(request.DefaultChannel)) state.DefaultChannel = request.DefaultChannel.Trim();
                if (!string.IsNullOrWhiteSpace(request.DefaultSessionPrefix)) state.DefaultSessionPrefix = request.DefaultSessionPrefix.Trim();
                if (request.AutoTriggerAgentTurn.HasValue) state.AutoTriggerAgentTurn = request.AutoTriggerAgentTurn.Value;
                if (request.ForwardToMatrixRoom.HasValue) state.ForwardToMatrixRoom = request.ForwardToMatrixRoom.Value;
                if (request.RateLimitPerMinute.HasValue) state.RateLimitPerMinute = Math.Clamp(request.RateLimitPerMinute.Value, 5, 1000);
                if (request.DeduplicationWindowSeconds.HasValue) state.DeduplicationWindowSeconds = Math.Clamp(request.DeduplicationWindowSeconds.Value, 10, 3600);
                if (!string.IsNullOrWhiteSpace(request.PromptTemplate)) state.PromptTemplate = request.PromptTemplate;
                if (request.OutboundWebhooks is not null)
                {
                    // Preserve existing secrets if masked "********" is passed
                    var existingMap = state.OutboundWebhooks.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
                    var updatedList = new List<OutboundWebhookTargetDto>();
                    foreach (var item in request.OutboundWebhooks)
                    {
                        var id = string.IsNullOrWhiteSpace(item.Id) ? $"out_{Guid.NewGuid():N}"[..10] : item.Id;
                        var sec = item.Secret;
                        if (sec == SecretRedaction.Mask && existingMap.TryGetValue(id, out var prev))
                        {
                            sec = prev.Secret;
                        }
                        updatedList.Add(item with { Id = id, Secret = sec });
                    }
                    state.OutboundWebhooks = updatedList;
                }
            });

            // If Matrix channel settings were updated, push them down to QwenPaw runtime /api/agents/{profile}/config/channels/matrix
            if (request.MatrixChannelEnabled.HasValue || !string.IsNullOrWhiteSpace(request.MatrixHomeserver) || !string.IsNullOrWhiteSpace(request.MatrixUserId))
            {
                var target = routing.ResolveTarget(cleanAgent);
                if (target.IsQwenPaw && target.Instance is not null)
                {
                    try
                    {
                        using var getResp = await routing.SendAsync(
                            target.Instance,
                            HttpMethod.Get,
                            $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/config/channels/matrix",
                            agentProfileHeader: target.TargetAgentProfile,
                            timeout: TimeSpan.FromSeconds(10),
                            ct: ct);

                        if (getResp.IsSuccessStatusCode)
                        {
                            var matrixNode = await getResp.Content.ReadFromJsonAsync<JsonObject>(ct) ?? new JsonObject();
                            if (request.MatrixChannelEnabled.HasValue) matrixNode["enabled"] = request.MatrixChannelEnabled.Value;
                            if (!string.IsNullOrWhiteSpace(request.MatrixHomeserver)) matrixNode["homeserver"] = request.MatrixHomeserver.Trim();
                            if (!string.IsNullOrWhiteSpace(request.MatrixUserId)) matrixNode["user_id"] = request.MatrixUserId.Trim();

                            using var putResp = await routing.SendAsync(
                                target.Instance,
                                HttpMethod.Put,
                                $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/config/channels/matrix",
                                () => new StringContent(matrixNode.ToJsonString(), Encoding.UTF8, "application/json"),
                                agentProfileHeader: target.TargetAgentProfile,
                                timeout: TimeSpan.FromSeconds(10),
                                ct: ct);

                            cache.Remove($"agent360:proxy:{cleanAgent}:config/channels");
                        }
                    }
                    catch
                    {
                        // Non-blocking if runtime channel update is unsupported
                    }
                }
            }

            var updatedDto = await BuildConfigDtoAsync(cleanAgent, httpContext, store, routing, ct);
            return Results.Ok(updatedDto);
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("UpdateAgentWebhookConfig")
        .WithTags(EndpointTags.Agents)
        .WithSummary("Update agent webhook settings and synchronize Matrix channel config to QwenPaw runtime")
        .AllowAnonymous();

        // 3. POST /api/v1/agents/{agentId}/webhooks/rotate-secret
        routeBuilder.MapPost("agents/{agentId}/webhooks/rotate-secret", async (
            string agentId,
            HttpContext httpContext,
            AgentWebhookStore store,
            IFleetInstanceRoutingService routing,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var (newSecret, _) = await store.RotateSecretAsync(agentId);
            var dto = await BuildConfigDtoAsync(agentId, httpContext, store, routing, ct);
            return Results.Ok(new
            {
                success = true,
                agent_id = dto.AgentId,
                new_secret = newSecret,
                config = dto,
                message = "Đã xoay vòng (rotate) khóa ký HMAC Webhook thành công. Hãy lưu lại khóa mới ngay lập tức."
            });
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("RotateAgentWebhookSecret")
        .WithTags(EndpointTags.Agents)
        .WithSummary("Rotate HMAC signing secret for an agent's inbound webhook endpoint")
        .AllowAnonymous();

        // 4. GET /api/v1/agents/{agentId}/webhooks/deliveries
        routeBuilder.MapGet("agents/{agentId}/webhooks/deliveries", async (
            string agentId,
            [FromQuery] int limit,
            HttpContext httpContext,
            AgentWebhookStore store,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var list = store.GetDeliveries(agentId, limit <= 0 ? 40 : limit);
            return Results.Ok(list);
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("GetAgentWebhookDeliveries")
        .WithTags(EndpointTags.Agents)
        .WithSummary("List recent inbound/outbound webhook deliveries and QwenPaw dispatch logs for an agent")
        .AllowAnonymous();

        // 4b. POST /api/v1/agents/{agentId}/webhooks/deliveries/{deliveryId}/replay
        // Re-triggers delivery for a previous webhook event
        routeBuilder.MapPost("agents/{agentId}/webhooks/deliveries/{deliveryId}/replay", async (
            string agentId,
            string deliveryId,
            HttpContext httpContext,
            AgentWebhookStore store,
            HttpClient httpClient,
            SessionMultiplexerHub multiplexerHub,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var cleanAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
            var delivery = store.GetDeliveryById(deliveryId);
            if (delivery is null || !string.Equals(delivery.AgentId, cleanAgent, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { message = $"Không tìm thấy bản ghi webhook delivery '{deliveryId}' cho agent '{cleanAgent}'" });
            }

            if (delivery.Direction.Equals("outbound", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(delivery.TargetUrl) && !string.IsNullOrWhiteSpace(delivery.RawPayload))
            {
                var state = store.GetOrCreateState(cleanAgent);
                var target = state.OutboundWebhooks.FirstOrDefault(w => string.Equals(w.Url, delivery.TargetUrl, StringComparison.OrdinalIgnoreCase));
                var sec = target?.Secret;
                var replayed = await ExecuteOutboundWithBackoffAsync(
                    cleanAgent,
                    delivery.Source,
                    delivery.TargetUrl,
                    sec,
                    target?.Headers,
                    delivery.EventType,
                    delivery.SessionId,
                    delivery.RawPayload,
                    httpClient,
                    store,
                    multiplexerHub,
                    maxAttempts: 3);

                return Results.Ok(new
                {
                    success = replayed.HttpStatus is >= 200 and < 300,
                    message = $"Đã phát lại (replay) Outbound Webhook '{delivery.EventType}' -> {delivery.TargetUrl} ({replayed.Status})",
                    delivery = replayed
                });
            }

            return Results.BadRequest(new { message = "Chỉ hỗ trợ phát lại (replay) cho các sự kiện Outbound Webhook có lưu payload" });
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("ReplayAgentWebhookDelivery")
        .WithTags(EndpointTags.Agents)
        .WithSummary("Replay a previous webhook delivery record with backoff retry")
        .AllowAnonymous();

        // 5. POST /api/v1/agents/{agentId}/webhooks/probe-qwenpaw
        // Deep health & configuration check from Agent360 down to the target agent's QwenPaw API
        routeBuilder.MapPost("agents/{agentId}/webhooks/probe-qwenpaw", async (
            string agentId,
            HttpContext httpContext,
            IFleetInstanceRoutingService routing,
            AgentWebhookStore store,
            SessionMultiplexerHub multiplexerHub,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var cleanAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
            var target = routing.ResolveTarget(cleanAgent);
            var sw = Stopwatch.StartNew();

            if (!target.IsQwenPaw || target.Instance is null)
            {
                sw.Stop();
                var nonQp = new QwenPawBridgeProbeResultDto(
                    cleanAgent,
                    target.ClusterNode,
                    target.InstanceName,
                    false,
                    true,
                    "edge-gateway",
                    sw.ElapsedMilliseconds,
                    ["console", "matrix"],
                    true,
                    false,
                    0,
                    DateTimeOffset.UtcNow.ToString("O"),
                    $"Agent '{cleanAgent}' vận hành trên {target.InstanceName} (Edge/Gateway mode).");
                return Results.Ok(nonQp);
            }

            string runtimeStatus = "unreachable";
            int runningTasks = 0;
            bool reachable = false;
            var activeChannels = new List<string>();
            bool matrixConfigured = false;
            bool heartbeatEnabled = false;

            try
            {
                var statusTask = routing.SendAsync(
                    target.Instance,
                    HttpMethod.Get,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/agent-status",
                    agentProfileHeader: target.TargetAgentProfile,
                    timeout: TimeSpan.FromSeconds(8),
                    ct: ct);

                var channelsTask = routing.SendAsync(
                    target.Instance,
                    HttpMethod.Get,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/config/channels",
                    agentProfileHeader: target.TargetAgentProfile,
                    timeout: TimeSpan.FromSeconds(8),
                    ct: ct);

                var heartbeatTask = routing.SendAsync(
                    target.Instance,
                    HttpMethod.Get,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/config/heartbeat",
                    agentProfileHeader: target.TargetAgentProfile,
                    timeout: TimeSpan.FromSeconds(8),
                    ct: ct);

                await Task.WhenAll(statusTask, channelsTask, heartbeatTask);

                using var statusResp = await statusTask;
                using var channelsResp = await channelsTask;
                using var hbResp = await heartbeatTask;

                if (statusResp.IsSuccessStatusCode)
                {
                    reachable = true;
                    var stNode = await statusResp.Content.ReadFromJsonAsync<JsonObject>(ct);
                    runtimeStatus = stNode?["status"]?.GetValue<string>() ?? "idle";
                    runningTasks = stNode?["running_task_count"]?.GetValue<int>() ?? 0;
                }

                if (channelsResp.IsSuccessStatusCode)
                {
                    reachable = true;
                    var chNode = await channelsResp.Content.ReadFromJsonAsync<JsonObject>(ct);
                    if (chNode is not null)
                    {
                        foreach (var (chKey, chVal) in chNode)
                        {
                            if (chVal is JsonObject obj)
                            {
                                if (obj["enabled"]?.GetValue<bool>() == true)
                                {
                                    activeChannels.Add(chKey);
                                }
                                if (chKey.Equals("matrix", StringComparison.OrdinalIgnoreCase))
                                {
                                    var hs = obj["homeserver"]?.GetValue<string>();
                                    var uid = obj["user_id"]?.GetValue<string>();
                                    matrixConfigured = !string.IsNullOrWhiteSpace(hs) && !string.IsNullOrWhiteSpace(uid);
                                }
                            }
                        }
                    }
                }

                if (hbResp.IsSuccessStatusCode)
                {
                    var hbNode = await hbResp.Content.ReadFromJsonAsync<JsonObject>(ct);
                    heartbeatEnabled = hbNode?["enabled"]?.GetValue<bool>() == true;
                }
            }
            catch (Exception ex)
            {
                runtimeStatus = $"error: {ex.Message}";
            }

            sw.Stop();
            var deliveryRecord = new AgentWebhookDeliveryRecordDto(
                Id: $"whd_{Guid.NewGuid():N}"[..12],
                AgentId: cleanAgent,
                Direction: "qwenpaw_bridge",
                EventType: "qwenpaw.runtime.probe",
                Source: "agent360-control-plane",
                SessionId: null,
                Status: reachable ? "delivered" : "failed",
                HttpStatus: reachable ? 200 : 502,
                LatencyMs: sw.ElapsedMilliseconds,
                PayloadPreview: $"GET /api/agents/{target.TargetAgentProfile}/{{agent-status,config/channels,config/heartbeat}}",
                ResponsePreview: $"status={runtimeStatus}, channels=[{string.Join(",", activeChannels)}], matrix={matrixConfigured}, latency={sw.ElapsedMilliseconds}ms",
                SignatureVerified: true,
                Timestamp: DateTimeOffset.UtcNow.ToString("O"));

            store.RecordDelivery(deliveryRecord);

            var wsEvent = JsonSerializer.Serialize(new
            {
                type = "agent_webhook_delivery",
                agent_id = cleanAgent,
                delivery = deliveryRecord
            });
            _ = multiplexerHub.BroadcastAsync(wsEvent);

            var result = new QwenPawBridgeProbeResultDto(
                cleanAgent,
                target.ClusterNode,
                target.InstanceName,
                true,
                reachable,
                runtimeStatus,
                sw.ElapsedMilliseconds,
                activeChannels.ToArray(),
                matrixConfigured,
                heartbeatEnabled,
                runningTasks,
                DateTimeOffset.UtcNow.ToString("O"),
                reachable
                    ? $"✓ Kết nối QwenPaw API ({target.InstanceName} - Node {target.ClusterNode}) ổn định ({sw.ElapsedMilliseconds}ms). Trạng thái: {runtimeStatus.ToUpperInvariant()}."
                    : $"⚠ Không thể kết nối tới QwenPaw runtime cho {cleanAgent}.");

            return Results.Ok(result);
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("ProbeAgentQwenPawWebhookBridge")
        .WithTags(EndpointTags.Agents)
        .WithSummary("Probe live QwenPaw API connectivity, channel readiness, and runtime status for an agent")
        .AllowAnonymous();

        // 6. POST /api/v1/agents/{agentId}/webhooks/test
        // Dispatches a signed test webhook event through the pipeline and into QwenPaw runtime
        routeBuilder.MapPost("agents/{agentId}/webhooks/test", async (
            string agentId,
            [FromBody] AgentInboundWebhookPayload? testRequest,
            HttpContext httpContext,
            AgentWebhookStore store,
            IQwenPawClient qwenPawClient,
            IFleetInstanceRoutingService routing,
            SessionQueueManager queueManager,
            SessionMultiplexerHub multiplexerHub,
            HttpClient httpClient,
            IMemoryCache cache,
            ILogger<AgentWebhookEndpoints> logger,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var cleanAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
            var state = store.GetOrCreateState(cleanAgent);

            var payload = testRequest ?? new AgentInboundWebhookPayload(
                EventId: $"evt_test_{Guid.NewGuid():N}"[..16],
                EventType: "task.trigger",
                Source: "agent360-webhook-tester",
                SessionId: $"{state.DefaultSessionPrefix}-test",
                Channel: state.DefaultChannel,
                Message: $"Kiểm thử kết nối Webhook -> QwenPaw API cho agent {cleanAgent}. Hãy phản hồi ngắn gọn 1 câu xác nhận đã nhận tín hiệu.",
                TriggerTurn: true,
                WaitForCompletion: true);

            return await ExecuteInboundWebhookAsync(
                cleanAgent,
                payload,
                rawBody: JsonSerializer.Serialize(payload),
                signatureVerified: true,
                isInternalTest: true,
                httpContext,
                store,
                qwenPawClient,
                routing,
                queueManager,
                multiplexerHub,
                httpClient,
                cache,
                logger,
                ct);
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("TestAgentWebhookDispatch")
        .WithTags(EndpointTags.Agents)
        .WithSummary("Simulate and dispatch a signed webhook event into the agent's QwenPaw runtime")
        .AllowAnonymous();

        // 7. POST /api/v1/webhooks/agents/{agentId} & POST /api/v1/agents/{agentId}/webhooks/inbound
        // Public/External Inbound Webhook Receiver for a specific agent
        foreach (var pattern in new[] { "webhooks/agents/{agentId}", "agents/{agentId}/webhooks/inbound" })
        {
            routeBuilder.MapPost(pattern, async (
                string agentId,
                HttpContext httpContext,
                AgentWebhookStore store,
                IQwenPawClient qwenPawClient,
                IFleetInstanceRoutingService routing,
                SessionQueueManager queueManager,
                SessionMultiplexerHub multiplexerHub,
                HttpClient httpClient,
                IMemoryCache cache,
                ILogger<AgentWebhookEndpoints> logger,
                CancellationToken ct) =>
            {
                var cleanAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
                var state = store.GetOrCreateState(cleanAgent);

                if (!state.Enabled)
                {
                    return Results.Problem(
                        detail: $"Inbound webhooks are currently disabled for agent '{cleanAgent}'.",
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Webhook Disabled");
                }

                httpContext.Request.EnableBuffering();
                using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
                var rawBody = await reader.ReadToEndAsync(ct);
                httpContext.Request.Body.Position = 0;

                if (string.IsNullOrWhiteSpace(rawBody))
                {
                    return Results.BadRequest(new { error = "empty_payload" });
                }

                // Verify HMAC signature if RequireHmac is enabled or if signature header is supplied
                var hasSigHeader = httpContext.Request.Headers.TryGetValue(SignatureHeader, out var sigHeaderVal) ||
                                   httpContext.Request.Headers.TryGetValue(AltSignatureHeader, out sigHeaderVal);
                bool signatureVerified = false;

                if (state.RequireHmac || hasSigHeader)
                {
                    if (!hasSigHeader || string.IsNullOrWhiteSpace(sigHeaderVal))
                    {
                        RecordRejected(store, multiplexerHub, cleanAgent, "missing_signature", rawBody, 401);
                        return Results.Problem(
                            detail: $"Missing required '{SignatureHeader}' HMAC-SHA256 signature header.",
                            statusCode: StatusCodes.Status401Unauthorized,
                            title: "Missing Webhook Signature");
                    }

                    var tsStr = httpContext.Request.Headers.TryGetValue(TimestampHeader, out var tsVal) ? tsVal.ToString() : "";
                    if (!string.IsNullOrWhiteSpace(tsStr) && long.TryParse(tsStr, out var tsUnix))
                    {
                        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tsUnix) > state.DeduplicationWindowSeconds)
                        {
                            RecordRejected(store, multiplexerHub, cleanAgent, "stale_timestamp", rawBody, 401);
                            return Results.Problem(
                                detail: "Webhook timestamp skew exceeds configured tolerance window.",
                                statusCode: StatusCodes.Status401Unauthorized,
                                title: "Stale Webhook Timestamp");
                        }
                    }

                    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(state.SigningSecret));
                    var canonicalPayload = !string.IsNullOrWhiteSpace(tsStr) ? $"{tsStr}.{rawBody}" : rawBody;
                    var computedHex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
                    var computedRawHex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
                    var providedHex = sigHeaderVal.ToString().Replace("sha256=", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();

                    var matchCanonical = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedHex), Encoding.UTF8.GetBytes(providedHex));
                    var matchRaw = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedRawHex), Encoding.UTF8.GetBytes(providedHex));

                    if (!matchCanonical && !matchRaw)
                    {
                        RecordRejected(store, multiplexerHub, cleanAgent, "invalid_signature", rawBody, 401);
                        return Results.Problem(
                            detail: "HMAC-SHA256 signature verification failed.",
                            statusCode: StatusCodes.Status401Unauthorized,
                            title: "Invalid Webhook Signature");
                    }

                    signatureVerified = true;
                }

                AgentInboundWebhookPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<AgentInboundWebhookPayload>(rawBody);
                }
                catch
                {
                    RecordRejected(store, multiplexerHub, cleanAgent, "invalid_json", rawBody, 400);
                    return Results.BadRequest(new { error = "invalid_json" });
                }

                if (payload is null)
                {
                    return Results.BadRequest(new { error = "empty_payload" });
                }

                return await ExecuteInboundWebhookAsync(
                    cleanAgent,
                    payload,
                    rawBody,
                    signatureVerified,
                    isInternalTest: false,
                    httpContext,
                    store,
                    qwenPawClient,
                    routing,
                    queueManager,
                    multiplexerHub,
                    httpClient,
                    cache,
                    logger,
                    ct);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithTags(EndpointTags.Agents)
            .WithSummary("Inbound webhook receiver for an individual agent with HMAC verification and QwenPaw turn bridging")
            .AllowAnonymous();
        }
    }

    private static void RecordRejected(
        AgentWebhookStore store,
        SessionMultiplexerHub multiplexerHub,
        string agentId,
        string reason,
        string rawBody,
        int status)
    {
        var rec = new AgentWebhookDeliveryRecordDto(
            Id: $"whd_{Guid.NewGuid():N}"[..12],
            AgentId: agentId,
            Direction: "inbound",
            EventType: $"rejected.{reason}",
            Source: "external",
            SessionId: null,
            Status: "rejected",
            HttpStatus: status,
            LatencyMs: 1,
            PayloadPreview: rawBody.Length > 180 ? rawBody[..180] + "..." : rawBody,
            ResponsePreview: $"Rejected ({reason})",
            SignatureVerified: false,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"));

        store.RecordDelivery(rec);
        _ = multiplexerHub.BroadcastAsync(JsonSerializer.Serialize(new
        {
            type = "agent_webhook_delivery",
            agent_id = agentId,
            delivery = rec
        }));
    }

    private static async Task<IResult> ExecuteInboundWebhookAsync(
        string cleanAgent,
        AgentInboundWebhookPayload payload,
        string rawBody,
        bool signatureVerified,
        bool isInternalTest,
        HttpContext httpContext,
        AgentWebhookStore store,
        IQwenPawClient qwenPawClient,
        IFleetInstanceRoutingService routing,
        SessionQueueManager queueManager,
        SessionMultiplexerHub multiplexerHub,
        HttpClient httpClient,
        IMemoryCache cache,
        ILogger logger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var state = store.GetOrCreateState(cleanAgent);

        var eventId = !string.IsNullOrWhiteSpace(payload.EventId)
            ? payload.EventId!
            : $"evt_{Guid.NewGuid():N}"[..14];
        var eventType = !string.IsNullOrWhiteSpace(payload.EventType) ? payload.EventType!.Trim() : "task.trigger";
        var source = !string.IsNullOrWhiteSpace(payload.Source) ? payload.Source!.Trim() : (isInternalTest ? "agent360-ui-test" : "external-webhook");

        // 1. Deduplication check
        var dedupeKey = $"agent360:webhook:dedupe:{cleanAgent}:{eventId}";
        if (cache.TryGetValue(dedupeKey, out _))
        {
            sw.Stop();
            var dupRecord = new AgentWebhookDeliveryRecordDto(
                Id: $"whd_{Guid.NewGuid():N}"[..12],
                AgentId: cleanAgent,
                Direction: "inbound",
                EventType: eventType,
                Source: source,
                SessionId: payload.SessionId,
                Status: "duplicate",
                HttpStatus: 200,
                LatencyMs: sw.ElapsedMilliseconds,
                PayloadPreview: rawBody.Length > 180 ? rawBody[..180] + "..." : rawBody,
                ResponsePreview: "Duplicate event_id dropped by anti-replay guard",
                SignatureVerified: signatureVerified,
                Timestamp: DateTimeOffset.UtcNow.ToString("O"));
            store.RecordDelivery(dupRecord);
            return Results.Ok(new { status = "duplicate", event_id = eventId, agent_id = cleanAgent });
        }
        cache.Set(dedupeKey, true, TimeSpan.FromSeconds(Math.Max(15, state.DeduplicationWindowSeconds)));

        var sessionId = !string.IsNullOrWhiteSpace(payload.SessionId)
            ? payload.SessionId!.Trim()
            : $"{state.DefaultSessionPrefix}-{DateTimeOffset.UtcNow:yyyyMMdd}";
        var channel = !string.IsNullOrWhiteSpace(payload.Channel)
            ? payload.Channel!.Trim()
            : state.DefaultChannel;

        var rawMessage = !string.IsNullOrWhiteSpace(payload.Message)
            ? payload.Message!.Trim()
            : (!string.IsNullOrWhiteSpace(payload.Prompt)
                ? payload.Prompt!.Trim()
                : (payload.Data.HasValue ? payload.Data.Value.GetRawText() : "Webhook event triggered."));

        var formattedPrompt = (state.PromptTemplate ?? "[WEBHOOK EVENT: {{event_type}} | Source: {{source}}]\n{{message}}")
            .Replace("{{event_type}}", eventType, StringComparison.OrdinalIgnoreCase)
            .Replace("{{source}}", source, StringComparison.OrdinalIgnoreCase)
            .Replace("{{message}}", rawMessage, StringComparison.OrdinalIgnoreCase);

        // Record inbound user/webhook message into ChannelResponseStore so open chat streams see it immediately
        ChannelResponseStore.Record(
            agentId: cleanAgent,
            sessionId: sessionId,
            channel: channel,
            role: "user",
            content: formattedPrompt,
            status: "submitted",
            id: eventId);

        bool shouldTriggerTurn = payload.TriggerTurn ?? state.AutoTriggerAgentTurn;
        bool waitForCompletion = payload.WaitForCompletion ?? isInternalTest;
        string? assistantReply = null;
        string dispatchStatus = "delivered";
        int httpStatus = 200;

        if (shouldTriggerTurn)
        {
            var target = routing.ResolveTarget(cleanAgent);
            if (target.IsQwenPaw && target.Instance is not null)
            {
                if (waitForCompletion)
                {
                    try
                    {
                        queueManager.SetSessionTurnActive(sessionId, true, cleanAgent);
                        using var chatResp = await qwenPawClient.StreamConsoleChatAsync(
                            new ConsoleChatSendRequestDto(
                                SessionId: sessionId,
                                Message: formattedPrompt,
                                AgentId: cleanAgent,
                                UserId: payload.UserId ?? "webhook",
                                Input: null,
                                Channel: channel,
                                ModelSlotOverride: null,
                                LoopMode: payload.LoopMode ?? "default"),
                            ct);

                        httpStatus = (int)chatResp.StatusCode;
                        if (chatResp.IsSuccessStatusCode)
                        {
                            var sseRaw = await chatResp.Content.ReadAsStringAsync(ct);
                            assistantReply = ExtractAssistantTextFromSse(sseRaw);
                            dispatchStatus = "triggered";

                            if (!string.IsNullOrWhiteSpace(assistantReply))
                            {
                                ChannelResponseStore.Record(
                                    agentId: cleanAgent,
                                    sessionId: sessionId,
                                    channel: channel,
                                    role: "assistant",
                                    content: assistantReply,
                                    status: "completed");
                            }
                            qwenPawClient.InvalidateChatList(cleanAgent);
                        }
                        else
                        {
                            dispatchStatus = "failed";
                            assistantReply = $"QwenPaw returned HTTP {httpStatus}";
                        }
                    }
                    catch (Exception ex)
                    {
                        dispatchStatus = "failed";
                        httpStatus = 502;
                        assistantReply = $"QwenPaw dispatch error: {ex.Message}";
                        logger.LogWarning(ex, "[AgentWebhook] Failed to stream turn to QwenPaw for agent {Agent}", cleanAgent);
                    }
                    finally
                    {
                        queueManager.SetSessionTurnActive(sessionId, false, cleanAgent);
                    }
                }
                else
                {
                    // Fire-and-forget background turn on QwenPaw runtime
                    dispatchStatus = "triggered";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            queueManager.SetSessionTurnActive(sessionId, true, cleanAgent);
                            using var bgCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                            using var chatResp = await qwenPawClient.StreamConsoleChatAsync(
                                new ConsoleChatSendRequestDto(
                                    SessionId: sessionId,
                                    Message: formattedPrompt,
                                    AgentId: cleanAgent,
                                    UserId: payload.UserId ?? "webhook",
                                    Input: null,
                                    Channel: channel,
                                    ModelSlotOverride: null,
                                    LoopMode: payload.LoopMode ?? "default"),
                                bgCts.Token);

                            if (chatResp.IsSuccessStatusCode)
                            {
                                var sseRaw = await chatResp.Content.ReadAsStringAsync(bgCts.Token);
                                var reply = ExtractAssistantTextFromSse(sseRaw);
                                if (!string.IsNullOrWhiteSpace(reply))
                                {
                                    ChannelResponseStore.Record(
                                        agentId: cleanAgent,
                                        sessionId: sessionId,
                                        channel: channel,
                                        role: "assistant",
                                        content: reply,
                                        status: "completed");
                                }
                                qwenPawClient.InvalidateChatList(cleanAgent);
                            }
                        }
                        catch { }
                        finally
                        {
                            queueManager.SetSessionTurnActive(sessionId, false, cleanAgent);
                        }
                    });
                }
            }
        }

        // Trigger configured Outbound Webhooks if any match the event
        if (state.OutboundWebhooks is { Count: > 0 })
        {
            _ = DispatchOutboundWebhooksAsync(
                cleanAgent,
                eventId,
                eventType,
                sessionId,
                formattedPrompt,
                assistantReply,
                state.OutboundWebhooks,
                httpClient,
                store,
                multiplexerHub);
        }

        sw.Stop();
        var record = new AgentWebhookDeliveryRecordDto(
            Id: $"whd_{Guid.NewGuid():N}"[..12],
            AgentId: cleanAgent,
            Direction: "inbound",
            EventType: eventType,
            Source: source,
            SessionId: sessionId,
            Status: dispatchStatus,
            HttpStatus: httpStatus,
            LatencyMs: sw.ElapsedMilliseconds,
            PayloadPreview: formattedPrompt.Length > 200 ? formattedPrompt[..200] + "..." : formattedPrompt,
            ResponsePreview: !string.IsNullOrWhiteSpace(assistantReply)
                ? (assistantReply.Length > 240 ? assistantReply[..240] + "..." : assistantReply)
                : $"Session '{sessionId}' ({dispatchStatus})",
            SignatureVerified: signatureVerified,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"));

        store.RecordDelivery(record);

        var wsNotification = JsonSerializer.Serialize(new
        {
            type = "agent_webhook_delivery",
            agent_id = cleanAgent,
            delivery = record
        });
        _ = multiplexerHub.BroadcastAsync(wsNotification);

        return Results.Ok(new
        {
            success = httpStatus is >= 200 and < 300,
            status = dispatchStatus,
            event_id = eventId,
            agent_id = cleanAgent,
            session_id = sessionId,
            channel = channel,
            latency_ms = sw.ElapsedMilliseconds,
            assistant_reply = assistantReply,
            delivery = record
        });
    }

    private static async Task DispatchOutboundWebhooksAsync(
        string agentId,
        string eventId,
        string eventType,
        string sessionId,
        string prompt,
        string? assistantReply,
        List<OutboundWebhookTargetDto> targets,
        HttpClient httpClient,
        AgentWebhookStore store,
        SessionMultiplexerHub multiplexerHub)
    {
        foreach (var target in targets.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url)))
        {
            if (target.Events is { Length: > 0 } &&
                !target.Events.Contains("*") &&
                !target.Events.Any(e => string.Equals(e, eventType, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var bodyJson = JsonSerializer.Serialize(new
            {
                event_id = eventId,
                event_type = eventType,
                agent_id = agentId,
                session_id = sessionId,
                prompt,
                assistant_reply = assistantReply,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            });

            _ = Task.Run(async () =>
            {
                await ExecuteOutboundWithBackoffAsync(
                    agentId,
                    target.Name,
                    target.Url,
                    target.Secret,
                    target.Headers,
                    eventType,
                    sessionId,
                    bodyJson,
                    httpClient,
                    store,
                    multiplexerHub,
                    maxAttempts: 3);
            });
        }
    }

    private static async Task<AgentWebhookDeliveryRecordDto> ExecuteOutboundWithBackoffAsync(
        string agentId,
        string targetName,
        string targetUrl,
        string? secret,
        Dictionary<string, string>? headers,
        string eventType,
        string? sessionId,
        string bodyJson,
        HttpClient httpClient,
        AgentWebhookStore store,
        SessionMultiplexerHub multiplexerHub,
        int maxAttempts = 3)
    {
        int attempt = 0;
        int code = 0;
        string status = "delivered";
        string respPreview = "OK";
        long totalElapsedMs = 0;

        while (attempt < maxAttempts)
        {
            attempt++;
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                };

                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                req.Headers.TryAddWithoutValidation(TimestampHeader, ts);

                if (!string.IsNullOrWhiteSpace(secret))
                {
                    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                    var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{bodyJson}"))).ToLowerInvariant();
                    req.Headers.TryAddWithoutValidation(SignatureHeader, $"sha256={sig}");
                }

                if (headers is not null)
                {
                    foreach (var (hk, hv) in headers)
                    {
                        req.Headers.TryAddWithoutValidation(hk, hv);
                    }
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await httpClient.SendAsync(req, cts.Token);
                sw.Stop();
                totalElapsedMs += sw.ElapsedMilliseconds;
                code = (int)resp.StatusCode;
                status = resp.IsSuccessStatusCode ? "delivered" : "failed";
                var rText = await resp.Content.ReadAsStringAsync(cts.Token);
                respPreview = string.IsNullOrWhiteSpace(rText) ? $"HTTP {code}" : (rText.Length > 160 ? rText[..160] + "..." : rText);

                if (resp.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                totalElapsedMs += sw.ElapsedMilliseconds;
                code = 502;
                status = "failed";
                respPreview = ex.Message;
            }

            // Exponential backoff retry for transient network / 5xx failures: 1s, 2s
            if (attempt < maxAttempts && (code >= 500 || code == 0 || code == 429))
            {
                var delayMs = (int)(Math.Pow(2, attempt - 1) * 1000);
                await Task.Delay(delayMs);
            }
            else
            {
                break;
            }
        }

        var outRec = new AgentWebhookDeliveryRecordDto(
            Id: $"whd_{Guid.NewGuid():N}"[..12],
            AgentId: agentId,
            Direction: "outbound",
            EventType: eventType,
            Source: targetName,
            SessionId: sessionId,
            Status: status,
            HttpStatus: code,
            LatencyMs: totalElapsedMs,
            PayloadPreview: $"POST {targetUrl}",
            ResponsePreview: respPreview,
            SignatureVerified: !string.IsNullOrWhiteSpace(secret),
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            RetryCount: attempt - 1,
            TargetUrl: targetUrl,
            RawPayload: bodyJson);

        store.RecordDelivery(outRec);
        _ = multiplexerHub.BroadcastAsync(JsonSerializer.Serialize(new
        {
            type = "agent_webhook_delivery",
            agent_id = agentId,
            delivery = outRec
        }));

        return outRec;
    }

    private static string ExtractAssistantTextFromSse(string sseRaw)
    {
        if (string.IsNullOrWhiteSpace(sseRaw)) return "";
        var sb = new StringBuilder();
        string? lastFullText = null;

        foreach (var line in sseRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var jsonPart = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(jsonPart) || jsonPart == "[DONE]") continue;

            try
            {
                using var doc = JsonDocument.Parse(jsonPart);
                var root = doc.RootElement;

                if (root.TryGetProperty("object", out var objProp))
                {
                    var objType = objProp.GetString();
                    if (objType == "content" && root.TryGetProperty("delta", out var dProp) && dProp.GetBoolean())
                    {
                        if (root.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(tProp.GetString());
                        }
                    }
                    else if (objType == "message" && root.TryGetProperty("content", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in cArr.EnumerateArray())
                        {
                            if (c.TryGetProperty("type", out var ctProp) && ctProp.GetString() == "text" &&
                                c.TryGetProperty("text", out var txtProp) && txtProp.ValueKind == JsonValueKind.String)
                            {
                                var val = txtProp.GetString();
                                if (!string.IsNullOrWhiteSpace(val)) lastFullText = val;
                            }
                        }
                    }
                }
                else if (root.TryGetProperty("text", out var plainText) && plainText.ValueKind == JsonValueKind.String)
                {
                    sb.Append(plainText.GetString());
                }
            }
            catch { }
        }

        var accumulated = sb.ToString().Trim();
        return !string.IsNullOrWhiteSpace(accumulated) ? accumulated : (lastFullText?.Trim() ?? "");
    }

    private static async Task<AgentWebhookConfigDto> BuildConfigDtoAsync(
        string agentId,
        HttpContext httpContext,
        AgentWebhookStore store,
        IFleetInstanceRoutingService routing,
        CancellationToken ct)
    {
        var cleanAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
        var state = store.GetOrCreateState(cleanAgent);
        var target = routing.ResolveTarget(cleanAgent);

        var req = httpContext.Request;
        var origin = $"{req.Scheme}://{req.Host}";
        if (req.Host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            req.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            origin = "https://api-agent360.antares.vn";
        }

        var inboundUrl = $"{origin}/api/v1/webhooks/agents/{cleanAgent}";
        var agentTeamsUrl = $"{origin}/api/v1/webhooks/agentteams/events";

        bool matrixEnabled = false;
        string? matrixHomeserver = "https://matrix-core.hmz.one";
        string? matrixUserId = $"@{cleanAgent}:matrix-core.hmz.one";
        string runtimeStatus = target.IsQwenPaw ? "online" : "edge-gateway";

        if (target.IsQwenPaw && target.Instance is not null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));

                using var matrixResp = await routing.SendAsync(
                    target.Instance,
                    HttpMethod.Get,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/config/channels/matrix",
                    agentProfileHeader: target.TargetAgentProfile,
                    timeout: TimeSpan.FromSeconds(4),
                    ct: cts.Token);

                if (matrixResp.IsSuccessStatusCode)
                {
                    var node = await matrixResp.Content.ReadFromJsonAsync<JsonObject>(cts.Token);
                    if (node is not null)
                    {
                        matrixEnabled = node["enabled"]?.GetValue<bool>() ?? false;
                        matrixHomeserver = node["homeserver"]?.GetValue<string>() ?? matrixHomeserver;
                        matrixUserId = node["user_id"]?.GetValue<string>() ?? matrixUserId;
                    }
                }
            }
            catch
            {
                runtimeStatus = "cached";
            }
        }

        var sec = state.SigningSecret;
        var preview = sec.Length > 14 ? $"{sec[..10]}...{sec[^4..]}" : "whsec_****";

        var maskedOutbound = state.OutboundWebhooks.Select(w => w with
        {
            Secret = string.IsNullOrWhiteSpace(w.Secret) ? "" : SecretRedaction.Mask
        }).ToList();

        return new AgentWebhookConfigDto(
            AgentId: cleanAgent,
            Enabled: state.Enabled,
            RequireHmac: state.RequireHmac,
            SigningSecretMasked: SecretRedaction.Mask,
            SigningSecretPreview: preview,
            InboundWebhookUrl: inboundUrl,
            AgentTeamsWebhookUrl: agentTeamsUrl,
            AllowedEvents: state.AllowedEvents,
            DefaultChannel: state.DefaultChannel,
            DefaultSessionPrefix: state.DefaultSessionPrefix,
            AutoTriggerAgentTurn: state.AutoTriggerAgentTurn,
            ForwardToMatrixRoom: state.ForwardToMatrixRoom,
            RateLimitPerMinute: state.RateLimitPerMinute,
            DeduplicationWindowSeconds: state.DeduplicationWindowSeconds,
            PromptTemplate: state.PromptTemplate,
            OutboundWebhooks: maskedOutbound,
            MatrixChannelEnabled: matrixEnabled,
            MatrixHomeserver: matrixHomeserver,
            MatrixUserId: matrixUserId,
            QwenPawRuntimeStatus: runtimeStatus,
            QwenPawNode: $"{target.InstanceName} ({target.ClusterNode})",
            UpdatedAt: state.UpdatedAt);
    }
}
