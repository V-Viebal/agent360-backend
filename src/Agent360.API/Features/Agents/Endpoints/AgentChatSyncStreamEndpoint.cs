using System.Text.Json;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Features.Agents.SubAgents;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Endpoints;

public sealed record IngestChannelResponseDto(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("status")] string? Status = "completed",
    [property: JsonPropertyName("id")] string? Id = null);

public sealed class AgentChatSyncStreamEndpoint : IEndpoint
{
    public const string RouteName = "StreamAgentChatSessionSync";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. GET /api/v1/agents/{agentId}/chats/{chatId}/stream
        // Live Server-Sent Events stream: monitors session updates across channels (Telegram, console, cron, A2A, subagents)
        // Resolves chatId by either internal QwenPaw chat UUID or channel session identifier (e.g. telegram:1448508743).
        routeBuilder.MapGet(
            "agents/{agentId}/chats/{chatId}/stream",
            async (
                string agentId,
                string chatId,
                [FromQuery] string? channel,
                HttpContext httpContext,
                IQwenPawClient client,
                IConfiguration config,
                SessionQueueManager queueManager,
                CancellationToken ct) =>
            {
                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";

                var secOpt = httpContext.RequestServices.GetService<Microsoft.Extensions.Options.IOptionsMonitor<Agent360SecurityOptions>>()?.CurrentValue;
                if (secOpt?.RequireAuthForSensitiveReads == true)
                {
                    var (accessAllowed, accessReason) = await AgentOwnershipGuard.CheckAccessAsync(
                        agentId, httpContext, httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                        config, ct);
                    if (!accessAllowed)
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await httpContext.Response.WriteAsync($"data: {{\"error\": \"AGENT_ACCESS_DENIED\", \"message\": \"{accessReason}\"}}\n\n", ct);
                    }
                }

                int lastKnownCount = -1;
                string? lastKnownMessageId = null;
                string effectiveChatId = chatId;
                string detectedChannel = channel ?? "unknown";
                string? canonicalSessionId = null;

                // Resolve channel and canonical UUID if given a session identifier (e.g. telegram:xxx)
                try
                {
                    var chatsRaw = await client.GetAgentChatsRawAsync(agentId, ct);
                    var (resolvedId, resolvedChannel, resolvedSession) = ResolveTargetChat(chatId, chatsRaw);
                    effectiveChatId = resolvedId;
                    if (resolvedChannel is not null) detectedChannel = resolvedChannel;
                    canonicalSessionId = resolvedSession;
                }
                catch
                {
                    // Fall back to original chatId on resolution failure
                }

                var isInitiallyRunning = queueManager.IsSessionTurnActive(effectiveChatId) ||
                    (!string.IsNullOrWhiteSpace(canonicalSessionId) && queueManager.IsSessionTurnActive(canonicalSessionId));

                // Send initial connected ping with detected channel context
                var connectMeta = JsonSerializer.Serialize(new
                {
                    status = "connected",
                    agent_id = agentId,
                    chat_id = effectiveChatId,
                    session_id = canonicalSessionId ?? chatId,
                    channel = detectedChannel,
                    is_running = isInitiallyRunning
                });
                await httpContext.Response.WriteAsync($"event: connected\ndata: {connectMeta}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var rawJson = await client.GetChatHistoryRawAsync(effectiveChatId, agentId, ct);
                        if (!string.IsNullOrWhiteSpace(rawJson))
                        {
                            using var doc = JsonDocument.Parse(rawJson);
                            if (doc.RootElement.TryGetProperty("messages", out var msgsElem) && msgsElem.ValueKind == JsonValueKind.Array)
                            {
                                int currentCount = msgsElem.GetArrayLength();
                                string? currentLastId = null;
                                JsonElement? lastEl = null;

                                if (currentCount > 0)
                                {
                                    var el = msgsElem[currentCount - 1];
                                    lastEl = el;
                                    if (el.TryGetProperty("id", out var idProp))
                                    {
                                        currentLastId = idProp.GetString();
                                    }
                                }

                                if (lastKnownCount == -1)
                                {
                                    lastKnownCount = currentCount;
                                    lastKnownMessageId = currentLastId;

                                    // Check if active turn is in-progress
                                    if (lastEl.HasValue)
                                    {
                                        var el = lastEl.Value;
                                        var curStatus = el.TryGetProperty("status", out var sP) ? sP.GetString() : "completed";
                                        if (curStatus is "in_progress" or "running")
                                        {
                                            queueManager.SetSessionTurnActive(effectiveChatId, true);
                                            if (!string.IsNullOrWhiteSpace(canonicalSessionId))
                                                queueManager.SetSessionTurnActive(canonicalSessionId, true);
                                        }
                                    }
                                }
                                else if (currentCount != lastKnownCount || currentLastId != lastKnownMessageId)
                                {
                                    string? msgStatus = "completed";
                                    string role = "assistant";
                                    string extractedContent = "";

                                    if (lastEl.HasValue)
                                    {
                                        var el = lastEl.Value;
                                        if (el.TryGetProperty("status", out var sProp)) msgStatus = sProp.GetString();
                                        if (el.TryGetProperty("role", out var rProp)) role = rProp.GetString() ?? "assistant";
                                        if (el.TryGetProperty("content", out var cProp))
                                        {
                                            if (cProp.ValueKind == JsonValueKind.String)
                                            {
                                                extractedContent = cProp.GetString() ?? "";
                                            }
                                            else if (cProp.ValueKind == JsonValueKind.Array)
                                            {
                                                foreach (var part in cProp.EnumerateArray())
                                                {
                                                    if (part.TryGetProperty("text", out var tProp))
                                                    {
                                                        extractedContent += tProp.GetString();
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    // Track active turn state in SessionQueueManager
                                    var isTurnRunning = msgStatus is "in_progress" or "running";
                                    queueManager.SetSessionTurnActive(effectiveChatId, isTurnRunning);
                                    if (!string.IsNullOrWhiteSpace(canonicalSessionId))
                                        queueManager.SetSessionTurnActive(canonicalSessionId, isTurnRunning);

                                    // Record response event in telemetry / channel store for auditing and analytics
                                    ChannelResponseStore.Record(new ChannelResponseEvent(
                                        Id: currentLastId ?? Guid.NewGuid().ToString(),
                                        AgentId: agentId,
                                        SessionId: canonicalSessionId ?? chatId,
                                        Channel: detectedChannel,
                                        Role: role,
                                        Content: extractedContent,
                                        Status: msgStatus ?? "completed",
                                        RecordedAt: DateTime.UtcNow
                                    ));

                                    var payload = JsonSerializer.Serialize(new
                                    {
                                        type = "session_sync",
                                        chat_id = effectiveChatId,
                                        session_id = canonicalSessionId ?? chatId,
                                        agent_id = agentId,
                                        channel = detectedChannel,
                                        message_count = currentCount,
                                        is_running = isTurnRunning,
                                        last_message = new
                                        {
                                            id = currentLastId,
                                            role,
                                            content = extractedContent,
                                            status = msgStatus
                                        },
                                        messages = msgsElem
                                    });

                                    await httpContext.Response.WriteAsync($"event: session_update\ndata: {payload}\n\n", ct);
                                    await httpContext.Response.Body.FlushAsync(ct);

                                    lastKnownCount = currentCount;
                                    lastKnownMessageId = currentLastId;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore transient read or network blips during polling
                    }

                    // Keep-alive heartbeat comment every iteration
                    await httpContext.Response.WriteAsync(": keepalive\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);

                    await Task.Delay(1500, ct);
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("Live SSE stream to sync active chat session with Telegram and external turns in real-time")
            .AllowAnonymous();

        // 2. POST /api/v1/agents/{agentId}/channels/responses
        // Inbound API for external channels (Telegram bot, Zalo bot, Cron runner, A2A subagents) to record real-time response turns
        routeBuilder.MapPost(
            "agents/{agentId}/channels/responses",
            ([FromRoute] string agentId,
             [FromBody] IngestChannelResponseDto dto,
             SessionQueueManager queueManager) =>
            {
                var isRunning = dto.Status is "in_progress" or "running";
                queueManager.SetSessionTurnActive(dto.SessionId, isRunning);

                var recorded = ChannelResponseStore.Record(
                    agentId: agentId,
                    sessionId: dto.SessionId,
                    channel: dto.Channel,
                    role: dto.Role,
                    content: dto.Content,
                    status: dto.Status ?? "completed",
                    id: dto.Id
                );

                return TypedResults.Ok(recorded);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("IngestChannelResponse")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Ingest a real-time turn response from an external channel")
            .AllowAnonymous();

        // 3. GET /api/v1/agents/{agentId}/channels/responses/recent
        // Query recent real-time recorded responses across sessions for a specific agent
        routeBuilder.MapGet(
            "agents/{agentId}/channels/responses/recent",
            (string agentId, [FromQuery] string? channel, [FromQuery] int limit = 50) =>
            {
                var events = ChannelResponseStore.GetRecent(agentId, channel, limit);
                return TypedResults.Ok(events);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetRecentAgentChannelResponses")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get recent realtime channel response telemetry and turn events for an agent")
            .AllowAnonymous();

        // 4. GET /api/v1/channels/responses/recent
        // Query recent real-time recorded responses across all agents
        routeBuilder.MapGet(
            "channels/responses/recent",
            ([FromQuery] string? agentId, [FromQuery] string? channel, [FromQuery] int limit = 50) =>
            {
                var events = ChannelResponseStore.GetRecent(agentId, channel, limit);
                return TypedResults.Ok(events);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetRecentAllChannelResponses")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get recent realtime channel response telemetry across all channels and agents")
            .AllowAnonymous();

        // 5. GET /api/v1/chats/active
        // Query list of currently active / running session IDs
        routeBuilder.MapGet(
            "chats/active",
            (SessionQueueManager queueManager) =>
            {
                return TypedResults.Ok(queueManager.GetActiveSessionIds());
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetActiveChats")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get list of currently running chat session IDs")
            .AllowAnonymous();
    }

    private static (string EffectiveChatId, string? Channel, string? SessionId) ResolveTargetChat(string identifier, string? rawChatsJson)
    {
        if (string.IsNullOrWhiteSpace(rawChatsJson))
            return (identifier, null, null);

        try
        {
            using var doc = JsonDocument.Parse(rawChatsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                    var ch = el.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;

                    if (string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sid, identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        return (id ?? identifier, ch, sid);
                    }
                }
            }
        }
        catch
        {
            // Fail open on JSON parsing error
        }

        return (identifier, null, null);
    }
}
