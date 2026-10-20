using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agent360.API.Features.Agents.Analytics;

namespace Agent360.API.Features.Agents.Services;

public sealed class SessionMultiplexerHub : IDisposable
{
    private readonly SessionQueueManager _queueManager;
    private readonly ILogger<SessionMultiplexerHub> _logger;
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly ConcurrentDictionary<Guid, Channel<string>> _sseChannels = new();

    public SessionMultiplexerHub(SessionQueueManager queueManager, ILogger<SessionMultiplexerHub> logger)
    {
        _queueManager = queueManager;
        _logger = logger;

        _queueManager.OnSessionTurnChanged += HandleQueueTurnChanged;
        ChannelResponseStore.OnChannelResponseRecorded += HandleChannelResponse;
        ChannelResponseStore.OnUnreadChanged += HandleUnreadChanged;
    }

    public int ConnectedWebSocketsCount => _sockets.Count;
    public int ConnectedSseCount => _sseChannels.Count;

    private void HandleQueueTurnChanged(string sessionId, bool isRunning)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "session_turn_update",
            session_id = sessionId,
            is_running = isRunning,
            status = isRunning ? "running" : "idle",
            timestamp = DateTime.UtcNow.ToString("O")
        });

        _ = BroadcastAsync(payload);
    }

    private void HandleChannelResponse(ChannelResponseEvent ev)
    {
        var isRunning = string.Equals(ev.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ev.Status, "in_progress", StringComparison.OrdinalIgnoreCase);

        var payload = JsonSerializer.Serialize(new
        {
            type = "session_turn_update",
            id = ev.Id,
            agent_id = ev.AgentId,
            session_id = ev.SessionId,
            channel = ev.Channel,
            role = ev.Role,
            status = ev.Status,
            is_running = isRunning,
            content_snippet = !string.IsNullOrWhiteSpace(ev.Content) && ev.Content.Length > 80 ? ev.Content[..80] : ev.Content,
            timestamp = ev.RecordedAt.ToString("O")
        });

        _ = BroadcastAsync(payload);
    }

    private void HandleUnreadChanged(string sessionId, bool isUnread)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "session_unread_update",
            session_id = sessionId,
            is_unread = isUnread,
            timestamp = DateTime.UtcNow.ToString("O")
        });

        _ = BroadcastAsync(payload);
    }

    public async Task BroadcastAsync(string jsonMessage)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonMessage);
        var segment = new ArraySegment<byte>(bytes);

        // Broadcast to WebSockets
        foreach (var (id, ws) in _sockets)
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to send to WebSocket {ClientId}", id);
                    _sockets.TryRemove(id, out _);
                }
            }
            else
            {
                _sockets.TryRemove(id, out _);
            }
        }

        // Broadcast to SSE Channels
        foreach (var (id, ch) in _sseChannels)
        {
            ch.Writer.TryWrite(jsonMessage);
        }
    }

    public object CreateSnapshot()
    {
        return new
        {
            type = "active_sessions_snapshot",
            active_session_ids = _queueManager.GetActiveSessionIds(),
            unread_session_ids = ChannelResponseStore.GetUnreadSessionIds(),
            timestamp = DateTime.UtcNow.ToString("O")
        };
    }

    public async Task HandleWebSocketClientAsync(WebSocket webSocket, CancellationToken ct)
    {
        var clientId = Guid.NewGuid();
        _sockets[clientId] = webSocket;

        try
        {
            // 1. Send initial state snapshot immediately upon connection
            var snapshotJson = JsonSerializer.Serialize(CreateSnapshot());
            var initBytes = Encoding.UTF8.GetBytes(snapshotJson);
            await webSocket.SendAsync(new ArraySegment<byte>(initBytes), WebSocketMessageType.Text, true, ct);

            var buffer = new byte[4096];
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct);
                    break;
                }

                if (result.Count == 0) continue;
                var clientMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);

                try
                {
                    using var doc = JsonDocument.Parse(clientMsg);
                    var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;

                    if (action == "ping")
                    {
                        var pongBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "pong", timestamp = DateTime.UtcNow.ToString("O") }));
                        await webSocket.SendAsync(new ArraySegment<byte>(pongBytes), WebSocketMessageType.Text, true, ct);
                    }
                    else if (action == "get_snapshot")
                    {
                        var freshSnapshot = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(CreateSnapshot()));
                        await webSocket.SendAsync(new ArraySegment<byte>(freshSnapshot), WebSocketMessageType.Text, true, ct);
                    }
                    else if (action == "mark_read")
                    {
                        var sid = doc.RootElement.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(sid))
                        {
                            ChannelResponseStore.MarkRead(sid);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-json or malformed frames from client
                }
            }
        }
        finally
        {
            _sockets.TryRemove(clientId, out _);
        }
    }

    public ChannelReader<string> SubscribeSse(out Guid subscriptionId)
    {
        subscriptionId = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _sseChannels[subscriptionId] = ch;
        return ch.Reader;
    }

    public void UnsubscribeSse(Guid subscriptionId)
    {
        if (_sseChannels.TryRemove(subscriptionId, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        _queueManager.OnSessionTurnChanged -= HandleQueueTurnChanged;
        ChannelResponseStore.OnChannelResponseRecorded -= HandleChannelResponse;
        ChannelResponseStore.OnUnreadChanged -= HandleUnreadChanged;

        foreach (var ws in _sockets.Values)
        {
            try { ws.Dispose(); } catch {}
        }
        _sockets.Clear();

        foreach (var ch in _sseChannels.Values)
        {
            ch.Writer.TryComplete();
        }
        _sseChannels.Clear();
    }
}
