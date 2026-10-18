using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>
/// One CDP screencast per (browser, tab, size) shared by every viewer. Chromium encodes each frame once no
/// matter how many people watch; each viewer has a 1-slot "latest frame wins" queue so a slow phone drops
/// frames instead of stalling the shared session. The session stops shortly after the last viewer leaves.
/// </summary>
public sealed class ScreencastHub(ILogger<ScreencastHub> logger)
{
    private static readonly TimeSpan IdleLinger = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly object _gate = new();

    public int ActiveSessions => _sessions.Count;

    public int ViewerCount(string cdpUrl, string targetId, int maxWidth, int quality) =>
        _sessions.TryGetValue(Key(cdpUrl, targetId, maxWidth, quality), out var s) ? s.ViewerCount : 0;

    private static string Key(string cdpUrl, string targetId, int maxWidth, int quality) => $"{cdpUrl}|{targetId}|{maxWidth}|{quality}";

    /// <summary>Streams frames to <paramref name="client"/> until it disconnects or the CDP session ends.</summary>
    public async Task WatchAsync(string cdpUrl, string targetId, int maxWidth, int quality, WebSocket client, string? seedFrame, CancellationToken ct)
    {
        var viewer = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        Session session;
        lock (_gate)
        {
            session = _sessions.GetOrAdd(Key(cdpUrl, targetId, maxWidth, quality), k => new Session(k, cdpUrl, targetId, maxWidth, quality, this, logger));
            session.Add(viewer);
        }
        if (session.LastFrame is { } last)
        {
            viewer.Writer.TryWrite(last);
        }
        else if (seedFrame is not null)
        {
            viewer.Writer.TryWrite(Frame(seedFrame));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // View-only: client messages are drained and discarded, never forwarded to CDP.
        var drain = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (client.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    if ((await client.ReceiveAsync(buf, cts.Token)).MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            finally { cts.Cancel(); }
        });

        try
        {
            await foreach (var frame in viewer.Reader.ReadAllAsync(cts.Token))
            {
                using var send = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                send.CancelAfter(TimeSpan.FromSeconds(10));
                await client.SendAsync(frame, WebSocketMessageType.Text, true, send.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ChannelClosedException) { }
        finally
        {
            cts.Cancel();
            lock (_gate)
            {
                session.Remove(viewer);
            }
            if (client.State == WebSocketState.Open)
            {
                try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            }
            await drain;
        }
    }

    internal static byte[] Frame(string base64Jpeg) =>
        JsonSerializer.SerializeToUtf8Bytes(new { method = "Page.screencastFrame", @params = new { data = base64Jpeg } });

    private void Retire(Session s)
    {
        lock (_gate)
        {
            if (s.ViewerCount == 0)
            {
                _sessions.TryRemove(new KeyValuePair<string, Session>(s.Key, s));
                s.Stop();
            }
        }
    }

    private sealed class Session
    {
        private readonly List<Channel<byte[]>> _viewers = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly ScreencastHub _hub;
        private readonly ILogger _logger;
        private CancellationTokenSource? _linger;

        public Session(string key, string cdpUrl, string targetId, int maxWidth, int quality, ScreencastHub hub, ILogger logger)
        {
            Key = key;
            _hub = hub;
            _logger = logger;
            _ = RunAsync(cdpUrl, targetId, maxWidth, quality);
        }

        public string Key { get; }
        public byte[]? LastFrame { get; private set; }
        public int ViewerCount { get { lock (_viewers) return _viewers.Count; } }

        public void Add(Channel<byte[]> v)
        {
            lock (_viewers)
            {
                _viewers.Add(v);
                _linger?.Cancel();
                _linger = null;
            }
        }

        public void Remove(Channel<byte[]> v)
        {
            lock (_viewers)
            {
                _viewers.Remove(v);
                v.Writer.TryComplete();
                if (_viewers.Count > 0) return;
                _linger = new CancellationTokenSource();
                var token = _linger.Token;
                _ = Task.Delay(IdleLinger, token).ContinueWith(t => { if (!t.IsCanceled) _hub.Retire(this); }, TaskScheduler.Default);
            }
        }

        public void Stop() => _cts.Cancel();

        private void Broadcast(byte[] frame)
        {
            LastFrame = frame;
            lock (_viewers)
            {
                foreach (var v in _viewers) v.Writer.TryWrite(frame);
            }
        }

        private async Task RunAsync(string cdpUrl, string targetId, int maxWidth, int quality)
        {
            var ct = _cts.Token;
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                var cdp = new Uri(cdpUrl);
                await ws.ConnectAsync(new Uri($"ws://{cdp.Authority}/devtools/page/{Uri.EscapeDataString(targetId)}"), ct);
                await SendAsync(ws, new { id = 1, method = "Page.startScreencast", @params = new { format = "jpeg", quality, maxWidth, maxHeight = maxWidth, everyNthFrame = 2 } }, ct);
                var buffer = new byte[64 * 1024];
                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, ct);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buffer, 0, r.Count);
                        if (ms.Length > 16 * 1024 * 1024) throw new WebSocketException("CDP message exceeds limit");
                    } while (!r.EndOfMessage);
                    using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                    if (!doc.RootElement.TryGetProperty("method", out var m) || m.GetString() != "Page.screencastFrame") continue;
                    var p = doc.RootElement.GetProperty("params");
                    Broadcast(Frame(p.GetProperty("data").GetString() ?? ""));
                    // ACK at once: per-viewer queues absorb slow clients, the shared session never waits on one.
                    await SendAsync(ws, new { id = 2, method = "Page.screencastFrameAck", @params = new { sessionId = p.GetProperty("sessionId").GetInt32() } }, ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
            {
                if (!ct.IsCancellationRequested) _logger.LogInformation("Screencast session ended: {Error}", ex.Message);
            }
            finally
            {
                lock (_viewers)
                {
                    foreach (var v in _viewers) v.Writer.TryComplete();
                }
                lock (_hub._gate)
                {
                    _hub._sessions.TryRemove(new KeyValuePair<string, Session>(Key, this));
                }
            }
        }

        private static Task SendAsync(WebSocket ws, object message, CancellationToken ct) =>
            ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);
    }
}
