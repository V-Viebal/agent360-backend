using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agent360.API.Abstractions;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;

namespace Agent360.API.Features.Agents.Endpoints;

public sealed class ChatWebSocketEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.Map("agents/ws/chat", async (
            HttpContext context,
            IQwenPawClient qwenPawClient,
            SessionQueueManager queueManager,
            ILogger<ChatWebSocketEndpoint> logger,
            CancellationToken ct) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024 * 4];

            try
            {
                while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                        break;
                    }

                    var msgJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    if (string.IsNullOrWhiteSpace(msgJson)) continue;

                    using var doc = JsonDocument.Parse(msgJson);
                    var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "chat" : "chat";
                    var agentId = doc.RootElement.TryGetProperty("agent_id", out var ag) ? ag.GetString() ?? "mons" : "mons";
                    var sessionId = doc.RootElement.TryGetProperty("session_id", out var s) && !string.IsNullOrWhiteSpace(s.GetString()) 
                        ? s.GetString()! 
                        : $"session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

                    // Handle Interactive Interruption / Cancel Turn via WS
                    if (action == "stop" || action == "interrupt")
                    {
                        queueManager.SetSessionTurnActive(sessionId ?? "", false);
                        var stopOk = await qwenPawClient.StopChatAsync(new ConsoleChatStopRequestDto(sessionId ?? ""), ct);
                        var stopAck = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "stopped", success = stopOk, session_id = sessionId }));
                        await webSocket.SendAsync(new ArraySegment<byte>(stopAck), WebSocketMessageType.Text, true, ct);
                        continue;
                    }

                    // Handle normal chat message
                    var messageText = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

                    var (accessAllowed, accessReason) = await AgentOwnershipGuard.CheckAccessAsync(
                        agentId, context, context.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                        context.RequestServices.GetRequiredService<IConfiguration>(), ct);
                    if (!accessAllowed)
                    {
                        var errPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", error = "AGENT_ACCESS_DENIED", message = accessReason }));
                        await webSocket.SendAsync(new ArraySegment<byte>(errPayload), WebSocketMessageType.Text, true, ct);
                        continue;
                    }

                    if (!IamRevocationGuard.IsExecutionAllowed(agentId, out var reason))
                    {
                        var errPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", error = "IAM_REVOKED", message = reason }));
                        await webSocket.SendAsync(new ArraySegment<byte>(errPayload), WebSocketMessageType.Text, true, ct);
                        continue;
                    }

                    queueManager.SetSessionTurnActive(sessionId ?? "", true);
                    try
                    {
                        var sendReq = new ConsoleChatSendRequestDto(
                            SessionId: sessionId,
                            Message: messageText,
                            AgentId: agentId,
                            ModelSlotOverride: null,
                            Input: null);

                        using var responseStream = await qwenPawClient.StreamConsoleChatAsync(sendReq, ct);
                        if (!responseStream.IsSuccessStatusCode)
                        {
                            var errBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", status = (int)responseStream.StatusCode }));
                            await webSocket.SendAsync(new ArraySegment<byte>(errBytes), WebSocketMessageType.Text, true, ct);
                            continue;
                        }

                        using var stream = await responseStream.Content.ReadAsStreamAsync(ct);
                        using var reader = new StreamReader(stream);
                        string? line;
                        while ((line = await reader.ReadLineAsync(ct)) != null)
                        {
                            if (webSocket.State != WebSocketState.Open) break;
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var chunkBytes = Encoding.UTF8.GetBytes(line);
                            await webSocket.SendAsync(new ArraySegment<byte>(chunkBytes), WebSocketMessageType.Text, true, ct);
                        }

                        var doneMsg = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                        await webSocket.SendAsync(new ArraySegment<byte>(doneMsg), WebSocketMessageType.Text, true, ct);
                    }
                    finally
                    {
                        queueManager.SetSessionTurnActive(sessionId ?? "", false);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WebSocket chat connection closed or encountered error");
            }
        });
    }
}
