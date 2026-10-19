using System.Text.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Endpoints;

public sealed class AgentChatSyncStreamEndpoint : IEndpoint
{
    public const string RouteName = "StreamAgentChatSessionSync";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // GET /api/v1/agents/{agentId}/chats/{chatId}/stream
        // Live Server-Sent Events stream: monitors session updates (e.g. from Telegram, background cron, or subagents)
        // and pushes newly arrived messages / turns to the web client in real-time.
        routeBuilder.MapGet(
            "agents/{agentId}/chats/{chatId}/stream",
            async (
                string agentId,
                string chatId,
                HttpContext httpContext,
                IQwenPawClient client,
                IConfiguration config,
                CancellationToken ct) =>
            {
                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";

                var (accessAllowed, accessReason) = await AgentOwnershipGuard.CheckAccessAsync(
                    agentId, httpContext, httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                    config, ct);
                if (!accessAllowed)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await httpContext.Response.WriteAsync($"data: {{\"error\": \"AGENT_ACCESS_DENIED\", \"message\": \"{accessReason}\"}}\n\n", ct);
                    return;
                }

                int lastKnownCount = -1;
                string? lastKnownMessageId = null;

                // Send initial connected ping
                await httpContext.Response.WriteAsync(": connected\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var rawJson = await client.GetChatHistoryRawAsync(chatId, agentId, ct);
                        if (!string.IsNullOrWhiteSpace(rawJson))
                        {
                            using var doc = JsonDocument.Parse(rawJson);
                            if (doc.RootElement.TryGetProperty("messages", out var msgsElem) && msgsElem.ValueKind == JsonValueKind.Array)
                            {
                                int currentCount = msgsElem.GetArrayLength();
                                string? currentLastId = null;
                                if (currentCount > 0)
                                {
                                    var lastEl = msgsElem[currentCount - 1];
                                    if (lastEl.TryGetProperty("id", out var idProp))
                                    {
                                        currentLastId = idProp.GetString();
                                    }
                                }

                                if (lastKnownCount == -1)
                                {
                                    lastKnownCount = currentCount;
                                    lastKnownMessageId = currentLastId;
                                }
                                else if (currentCount > lastKnownCount || (currentLastId != null && currentLastId != lastKnownMessageId))
                                {
                                    // New turn detected from external channel (Telegram / cron / A2A)
                                    var payload = JsonSerializer.Serialize(new
                                    {
                                        type = "session_sync",
                                        chat_id = chatId,
                                        agent_id = agentId,
                                        message_count = currentCount,
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
    }
}
