using System.Net.WebSockets;
using System.Text.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Services;

namespace Agent360.API.Features.Agents.Endpoints;

public sealed class SessionMultiplexerEndpoint : IEndpoint
{
    public const string RouteName = "SessionMultiplexerStream";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. WebSocket Multiplexer: /api/v1/agents/ws/sessions
        // WebSocket handshakes arrive as HTTP GET with Upgrade: websocket
        routeBuilder.MapGet(
            "agents/ws/sessions",
            async (HttpContext context, SessionMultiplexerHub hub, CancellationToken ct) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Expected WebSocket upgrade request.", ct);
                    return;
                }

                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await hub.HandleWebSocketClientAsync(webSocket, ct);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SessionMultiplexerWebSocket")
            .WithTags(EndpointTags.Agents)
            .AllowAnonymous();

        // Alias for convenience: /api/v1/chats/ws
        routeBuilder.MapGet(
            "chats/ws",
            async (HttpContext context, SessionMultiplexerHub hub, CancellationToken ct) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Expected WebSocket upgrade request.", ct);
                    return;
                }

                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await hub.HandleWebSocketClientAsync(webSocket, ct);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ChatsWebSocket")
            .WithTags(EndpointTags.Agents)
            .AllowAnonymous();

        // 2. SSE Multiplexer: GET /api/v1/chats/stream
        // Provides single multiplexed server-sent events stream for all session turns across all agents
        routeBuilder.MapGet(
            "chats/stream",
            async (HttpContext httpContext, SessionMultiplexerHub hub, CancellationToken ct) =>
            {
                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";

                // Emit initial snapshot immediately
                var snapshot = JsonSerializer.Serialize(hub.CreateSnapshot());
                await httpContext.Response.WriteAsync($"event: snapshot\ndata: {snapshot}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);

                var reader = hub.SubscribeSse(out var subId);
                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var keepaliveTask = Task.Run(async () =>
                    {
                        while (!linkedCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(10000, linkedCts.Token);
                                await httpContext.Response.WriteAsync(": keepalive\n\n", linkedCts.Token);
                                await httpContext.Response.Body.FlushAsync(linkedCts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch
                            {
                                break;
                            }
                        }
                    }, linkedCts.Token);

                    while (await reader.WaitToReadAsync(ct))
                    {
                        while (reader.TryRead(out var msg))
                        {
                            await httpContext.Response.WriteAsync($"event: session_event\ndata: {msg}\n\n", ct);
                            await httpContext.Response.Body.FlushAsync(ct);
                        }
                    }

                    linkedCts.Cancel();
                    await keepaliveTask;
                }
                finally
                {
                    hub.UnsubscribeSse(subId);
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("Live multiplexed SSE stream for all session turns across all channels and agents")
            .AllowAnonymous();
    }
}
