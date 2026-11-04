using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>
/// One persistent CDP WebSocket session per (browser, tab, size) shared by every viewer and input dispatcher.
/// Keeps a warm connection to Chromium so mouse click, wheel scroll, keyboard input, and screencast frames
/// execute in ~15ms with zero per-click WebSocket handshake overhead.
/// </summary>
public sealed class ScreencastHub(ILogger<ScreencastHub> logger)
{
    private static readonly TimeSpan IdleLinger = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly object _gate = new();

    public int ActiveSessions => _sessions.Count;

    /// <summary>Most recent base64 JPEG any live session produced for this tab matching the requested viewport aspect, or null.</summary>
    public string? LatestFrame(string cdpUrl, string targetId, int reqW = 0, int reqH = 0, bool isMobileDevice = false)
    {
        var prefix = $"{cdpUrl}|{targetId}|";
        foreach (var (k, s) in _sessions)
        {
            if (k.StartsWith(prefix, StringComparison.Ordinal) && s.LastFrameBase64 is { } f)
            {
                if (reqW >= 320 && reqH >= 240 && !s.MatchesViewport(reqW, reqH, isMobileDevice))
                {
                    continue;
                }
                return f;
            }
        }
        return null;
    }

    public int ViewerCount(string cdpUrl, string targetId, int maxWidth, int quality) =>
        _sessions.TryGetValue(Key(cdpUrl, targetId, maxWidth), out var s) ? s.ViewerCount : 0;

    private static string Key(string cdpUrl, string targetId, int maxWidth) => $"{cdpUrl}|{targetId}|{maxWidth}";

    /// <summary>Find an active warm session for (cdpUrl, targetId) — or any warm session on the same cdpUrl if pageId was omitted — to dispatch instant input commands in ~5ms.</summary>
    public async Task<bool> TryDispatchOverWarmSessionAsync(string cdpUrl, string targetId, BrowserInputRequest req, CancellationToken ct)
    {
        var exactPrefix = $"{cdpUrl}|{targetId}|";
        foreach (var (k, s) in _sessions)
        {
            if (k.StartsWith(exactPrefix, StringComparison.Ordinal) && s.IsConnected)
            {
                if (await s.TryDispatchInputAsync(req, ct))
                {
                    return true;
                }
            }
        }
        if (string.IsNullOrEmpty(req.PageId))
        {
            var hostPrefix = $"{cdpUrl}|";
            foreach (var (k, s) in _sessions)
            {
                if (k.StartsWith(hostPrefix, StringComparison.Ordinal) && s.IsConnected)
                {
                    if (await s.TryDispatchInputAsync(req, ct))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>Execute new_tab, close_tab, or activate_tab over any warm CDP session on the same browser host (~15ms, zero TCP handshake).</summary>
    public async Task<(bool Ok, string? CreatedTargetId)> TryManageTabOverWarmSessionAsync(string cdpUrl, string fallbackTargetId, string? browserContextId, BrowserInputRequest req, CancellationToken ct)
    {
        var hostPrefix = $"{cdpUrl}|";
        foreach (var (k, s) in _sessions)
        {
            if (k.StartsWith(hostPrefix, StringComparison.Ordinal) && s.IsConnected)
            {
                var res = await s.TryManageTabAsync(fallbackTargetId, browserContextId, req, ct);
                if (res.Ok) return res;
            }
        }
        return (false, null);
    }

    /// <summary>Pre-warm background CDP screencast sessions for all open tabs of an agent so clicking any tab switches in 0ms.</summary>
    public void WarmTabsInBackground(string cdpUrl, IEnumerable<string> targetIds, int maxWidth, int quality, int vw, int vh, bool isMobileDevice = false)
    {
        if (vw < 320 || vh < 240) return;
        foreach (var tid in targetIds.Take(6))
        {
            if (string.IsNullOrEmpty(tid)) continue;
            var key = Key(cdpUrl, tid, maxWidth);
            lock (_gate)
            {
                if (!_sessions.ContainsKey(key))
                {
                    var s = new Session(key, cdpUrl, tid, maxWidth, quality, vw, vh, this, logger, isMobileDevice);
                    _sessions[key] = s;
                    s.StartWarmLinger();
                }
            }
        }
    }

    public readonly record struct StreamPacket(byte[] Payload, WebSocketMessageType MessageType);

    /// <summary>Streams frames to <paramref name="client"/> and processes bidirectional input commands until disconnect.</summary>

    public async Task WatchAsync(string cdpUrl, string targetId, int maxWidth, int quality, WebSocket client, string? seedFrame, CancellationToken ct, int initialWidth = 0, int initialHeight = 0, bool binaryFrames = false, bool isMobileDevice = false)
    {
        var viewer = Channel.CreateBounded<StreamPacket>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var controlViewer = Channel.CreateBounded<StreamPacket>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        Session session;
        lock (_gate)
        {
            session = _sessions.GetOrAdd(Key(cdpUrl, targetId, maxWidth), k => new Session(k, cdpUrl, targetId, maxWidth, quality, initialWidth, initialHeight, this, logger, isMobileDevice));
            session.Add(viewer, controlViewer, binaryFrames, initialWidth, initialHeight, isMobileDevice);
        }
        if (initialWidth < 320 || initialHeight < 240 || session.MatchesViewport(initialWidth, initialHeight, isMobileDevice))
        {
            if (binaryFrames && session.LastBinaryFrame is { } lastBin)
            {
                viewer.Writer.TryWrite(new StreamPacket(lastBin, WebSocketMessageType.Binary));
            }
            else if (!binaryFrames && session.LastFrame is { } lastTxt)
            {
                viewer.Writer.TryWrite(new StreamPacket(lastTxt, WebSocketMessageType.Text));
            }
            else if (seedFrame is not null)
            {
                viewer.Writer.TryWrite(binaryFrames
                    ? new StreamPacket(Convert.FromBase64String(seedFrame), WebSocketMessageType.Binary)
                    : new StreamPacket(Frame(seedFrame), WebSocketMessageType.Text));
            }
        }
        else if (seedFrame is not null)
        {
            viewer.Writer.TryWrite(binaryFrames
                ? new StreamPacket(Convert.FromBase64String(seedFrame), WebSocketMessageType.Binary)
                : new StreamPacket(Frame(seedFrame), WebSocketMessageType.Text));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Bidirectional: receive client JSON input messages (click, scroll, type, key, navigate, reload, back, forward) and forward immediately over the warm CDP socket!
        var drain = Task.Run(async () =>
        {
            var buf = new byte[8192];
            using var ms = new MemoryStream();
            try
            {
                while (client.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await client.ReceiveAsync(buf, cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage && ms.Length < 65536);

                    if (r.MessageType == WebSocketMessageType.Text && ms.Length > 0)
                    {
                        try
                        {
                            var inputReq = JsonSerializer.Deserialize<BrowserInputRequest>(ms.GetBuffer().AsSpan(0, (int)ms.Length), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (inputReq is not null && !string.IsNullOrEmpty(inputReq.Action))
                            {
                                var actLower = inputReq.Action.Trim().ToLowerInvariant();
                                if (actLower is "type" or "ime_commit" or "key" or "clipboard_paste" or "clipboard_copy" or "click" or "dblclick" or "rightclick" or "mousedown" or "mouseup")
                                {
                                    await session.TryDispatchInputAsync(inputReq, cts.Token);
                                }
                                else
                                {
                                    _ = session.TryDispatchInputAsync(inputReq, cts.Token);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            finally { cts.Cancel(); }
        });

        try
        {
            while (client.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                StreamPacket pkt;
                if (viewer.Reader.TryRead(out var framePkt))
                {
                    while (viewer.Reader.TryRead(out var newerFramePkt))
                    {
                        framePkt = newerFramePkt;
                    }
                    pkt = framePkt;
                }
                else if (controlViewer.Reader.TryRead(out var ctrlPkt))
                {
                    pkt = ctrlPkt;
                }
                else
                {
                    var frameWait = viewer.Reader.WaitToReadAsync(cts.Token).AsTask();
                    var ctrlWait = controlViewer.Reader.WaitToReadAsync(cts.Token).AsTask();
                    var finished = await Task.WhenAny(frameWait, ctrlWait);
                    if (!await finished) break;
                    continue;
                }

                using var send = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                send.CancelAfter(TimeSpan.FromSeconds(10));
                await client.SendAsync(pkt.Payload, pkt.MessageType, true, send.Token);

                // Flush any coalesced control messages after a frame without evicting the next frame
                while (controlViewer.Reader.TryRead(out var trailingCtrl))
                {
                    using var ctrlSend = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    ctrlSend.CancelAfter(TimeSpan.FromSeconds(5));
                    await client.SendAsync(trailingCtrl.Payload, trailingCtrl.MessageType, true, ctrlSend.Token);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ChannelClosedException) { }
        finally
        {
            cts.Cancel();
            lock (_gate)
            {
                session.Remove(viewer, controlViewer);
            }
            if (client.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token); } catch { }
            }
            try { await drain.WaitAsync(TimeSpan.FromMilliseconds(250)); } catch { }
        }
    }

    internal static (int W, int H) ReadJpegDimensions(ReadOnlySpan<byte> bytes) => Session.ReadJpegDimensions(bytes);
    internal static (int W, int H) NormalizeViewport(int rawW, int rawH, int maxSurfaceW = 1440, int maxSurfaceH = 813, bool isMobileDevice = false) => Session.NormalizeViewport(rawW, rawH, maxSurfaceW, maxSurfaceH, isMobileDevice);
    internal static double ComputeBackingScale(int cssW, int cssH, int maxSurfaceW = 1440, int maxSurfaceH = 813, bool isMobileDevice = false) => Session.ComputeBackingScale(cssW, cssH, maxSurfaceW, maxSurfaceH, isMobileDevice);

    internal static byte[] Frame(string base64Jpeg) =>
        JsonSerializer.SerializeToUtf8Bytes(new { method = "Page.screencastFrame", @params = new { data = base64Jpeg } });

    public void RetireTarget(string cdpUrl, string targetId)
    {
        var prefix = $"{cdpUrl}|{targetId}|";
        lock (_gate)
        {
            foreach (var (k, s) in _sessions)
            {
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _sessions.TryRemove(new KeyValuePair<string, Session>(k, s));
                    s.Stop();
                }
            }
        }
    }

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
        private readonly List<(Channel<StreamPacket> FrameChannel, Channel<StreamPacket> ControlChannel, bool Binary)> _viewers = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ScreencastHub _hub;
        private readonly ILogger _logger;
        private CancellationTokenSource? _linger;
        private ClientWebSocket? _ws;
        private int _cmdId = 1000;
        private double _viewportW = 1440;
        private double _viewportH = 813;
        private int _nativeSurfaceMaxW = 1440;
        private int _nativeSurfaceMaxH = 813;
        private double _backingScale = 1.0;
        private bool _isMobileViewport;
        private bool _hasTargetViewport;
        private int _currentQuality = 88;
        private long _lastCursorProbeTick;
        private string _lastCursor = "default";
        private long _lastScrollMetricsTick;
        private string _lastScrollJson = "";
        private long _activeMotionUntilTick;
        private int _inFlightPumpedCaptures;
        private long _lastPumpedSentTick;
        private int _latestPumpedRespId;
        private long _lastBroadcastTick;
        private long _lastScreencastFrameTick;
        private long _lastMetricsHealTick;
        private readonly object _scrollLock = new();
        private int _scrollEvalInFlight;
        private double _pendingScrollDx;
        private double _pendingScrollDy;
        private int _pendingScrollX;
        private int _pendingScrollY;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _pendingTargetCreates = new();

        private readonly string _cdpUrl;
        private readonly int _maxWidth;

        public Session(string key, string cdpUrl, string targetId, int maxWidth, int quality, int initialWidth, int initialHeight, ScreencastHub hub, ILogger logger, bool isMobileDevice = false)
        {
            Key = key;
            _cdpUrl = cdpUrl;
            _maxWidth = maxWidth;
            _hub = hub;
            _logger = logger;
            _currentQuality = Math.Clamp(quality, 52, 92);
            _isMobileViewport = isMobileDevice;
            if (initialWidth >= 320 && initialHeight >= 240)
            {
                var (nw, nh) = NormalizeViewport(initialWidth, initialHeight, _nativeSurfaceMaxW, _nativeSurfaceMaxH, isMobileDevice);
                _viewportW = nw;
                _viewportH = nh;
                _backingScale = ComputeBackingScale(nw, nh, _nativeSurfaceMaxW, _nativeSurfaceMaxH, isMobileDevice);
                _hasTargetViewport = true;
            }
            _ = RunAsync(cdpUrl, targetId, maxWidth, quality, initialWidth, initialHeight, isMobileDevice);
        }

        public string Key { get; }
        public byte[]? LastFrame { get; private set; }
        public byte[]? LastBinaryFrame { get; private set; }
        public string? LastFrameBase64 { get; private set; }
        public bool IsConnected => _ws?.State == WebSocketState.Open;
        internal static (int W, int H) NormalizeViewport(int rawW, int rawH, int maxSurfaceW = 1440, int maxSurfaceH = 813, bool isMobileDevice = false)
        {
            double w = Math.Max(320, rawW);
            double h = Math.Max(240, rawH);
            if (isMobileDevice)
            {
                if (w < 360)
                {
                    var up = 360.0 / w;
                    w = 360;
                    h *= up;
                }
                double boundW = Math.Clamp(maxSurfaceW, 640, 1920);
                double boundH = Math.Clamp(maxSurfaceH, 480, 1080);
                var scaleDown = Math.Min(1.0, Math.Min(boundW / w, boundH / h));
                if (scaleDown < 1.0)
                {
                    w *= scaleDown;
                    h *= scaleDown;
                }
                return (Math.Max(320, (int)Math.Round(w)), Math.Max(240, (int)Math.Round(h)));
            }

            // Desktop FullHD Scale: Always normalize the logical CSS viewport up to FullHD (1920px wide, or 1080px tall)
            // while preserving the exact aspect ratio of the client's browser panel!
            if (w < 1920)
            {
                var up = 1920.0 / w;
                w = 1920.0;
                h *= up;
            }
            var fhdCap = Math.Min(1.0, Math.Min(1920.0 / w, 1080.0 / h));
            if (fhdCap < 1.0)
            {
                w *= fhdCap;
                h *= fhdCap;
            }
            return (Math.Max(640, (int)Math.Round(w)), Math.Max(360, (int)Math.Round(h)));
        }

        internal static double ComputeBackingScale(int cssW, int cssH, int maxSurfaceW = 1440, int maxSurfaceH = 813, bool isMobileDevice = false)
        {
            double boundW = Math.Clamp(maxSurfaceW, 640, 1920);
            double boundH = Math.Clamp(maxSurfaceH, 480, 1080);
            var scale = Math.Min(1.0, Math.Min(boundW / Math.Max(1.0, cssW), boundH / Math.Max(1.0, cssH)));
            return Math.Round(Math.Clamp(scale, 0.35, 1.0), 4);
        }

        public bool MatchesViewport(int rawW, int rawH, bool? isMobileDevice = null)
        {
            var wantMobile = isMobileDevice ?? _isMobileViewport;
            if (wantMobile != _isMobileViewport) return false;
            var (w, h) = NormalizeViewport(rawW, rawH, _nativeSurfaceMaxW, _nativeSurfaceMaxH, wantMobile);
            return Math.Abs(_viewportW - w) <= 12 && Math.Abs(_viewportH - h) <= 12;
        }

        internal static (int W, int H) ReadJpegDimensions(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 11 || bytes[0] != 0xFF || bytes[1] != 0xD8) return (0, 0);
            int i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF)
                {
                    i++;
                    continue;
                }
                byte marker = bytes[i + 1];
                while (marker == 0xFF && i + 2 < bytes.Length)
                {
                    i++;
                    marker = bytes[i + 1];
                }
                if (marker is 0xC0 or 0xC1 or 0xC2)
                {
                    int h = (bytes[i + 5] << 8) | bytes[i + 6];
                    int w = (bytes[i + 7] << 8) | bytes[i + 8];
                    return (w, h);
                }
                if (marker is 0xD8 or 0xD9 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                }
                else
                {
                    int segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (segLen < 2) break;
                    i += 2 + segLen;
                }
            }
            return (0, 0);
        }

        private bool MatchesTargetFrameDimensions(ReadOnlySpan<byte> jpegBytes)
        {
            if (!_hasTargetViewport) return true;
            var (jw, jh) = ReadJpegDimensions(jpegBytes);
            if (jw <= 0 || jh <= 0) return true;
            var expectedBackingW = _viewportW * _backingScale;
            var expectedBackingH = _viewportH * _backingScale;
            var targetAspect = _viewportW / Math.Max(1.0, _viewportH);
            var frameAspect = (double)jw / Math.Max(1, jh);
            var matchesBacking = Math.Abs(jw - expectedBackingW) <= 18 && Math.Abs(jh - expectedBackingH) <= 18;
            return matchesBacking && Math.Abs(frameAspect - targetAspect) <= 0.035;
        }
        public int ViewerCount { get { lock (_viewers) return _viewers.Count; } }

        public void Add(Channel<StreamPacket> v, Channel<StreamPacket> ctrl, bool binary, int reqW = 0, int reqH = 0, bool isMobileDevice = false)
        {
            lock (_viewers)
            {
                _viewers.Add((v, ctrl, binary));
                _linger?.Cancel();
                _linger = null;
            }
            var (normReqW, normReqH) = reqW >= 320 && reqH >= 240 ? NormalizeViewport(reqW, reqH, _nativeSurfaceMaxW, _nativeSurfaceMaxH, isMobileDevice) : (0, 0);
            if (normReqW >= 320 && normReqH >= 240 && (_isMobileViewport != isMobileDevice || Math.Abs(_viewportW - normReqW) > 4 || Math.Abs(_viewportH - normReqH) > 4))
            {
                LastFrame = null;
                LastBinaryFrame = null;
                LastFrameBase64 = null;
                _ = TryDispatchInputAsync(new BrowserInputRequest("resize", Width: reqW, Height: reqH, IsMobile: isMobileDevice, DeviceType: isMobileDevice ? "phone" : "desktop"), _cts.Token);
            }
        }

        public void Remove(Channel<StreamPacket> v, Channel<StreamPacket> ctrl)
        {
            lock (_viewers)
            {
                _viewers.RemoveAll(x => ReferenceEquals(x.FrameChannel, v));
                v.Writer.TryComplete();
                ctrl.Writer.TryComplete();
                if (_viewers.Count > 0) return;
                _linger = new CancellationTokenSource();
                var token = _linger.Token;
                _ = Task.Delay(IdleLinger, token).ContinueWith(t => { if (!t.IsCanceled) _hub.Retire(this); }, TaskScheduler.Default);
            }
        }

        public void StartWarmLinger()
        {
            lock (_viewers)
            {
                if (_viewers.Count > 0) return;
                _linger?.Cancel();
                _linger = new CancellationTokenSource();
                var token = _linger.Token;
                _ = Task.Delay(IdleLinger, token).ContinueWith(t => { if (!t.IsCanceled) _hub.Retire(this); }, TaskScheduler.Default);
            }
        }

        public void Stop() => _cts.Cancel();

        public Task AdjustAdaptiveQualityAsync(int targetQuality, CancellationToken ct)
        {
            // Keep _currentQuality updated in the 55..75 range for the 60fps active motion frame pump without restarting Page.startScreencast mid-stream
            Interlocked.Exchange(ref _currentQuality, Math.Clamp(targetQuality, 55, 75));
            return Task.CompletedTask;
        }

        public async Task<(bool Ok, string? CreatedTargetId)> TryManageTabAsync(string fallbackTargetId, string? browserContextId, BrowserInputRequest req, CancellationToken ct)
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open) return (false, null);
            var action = (req.Action ?? "").Trim().ToLowerInvariant();
            try
            {
                if (action == "new_tab" && !string.IsNullOrEmpty(req.Text))
                {
                    var reqId = Interlocked.Increment(ref _cmdId);
                    var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingTargetCreates[reqId] = tcs;
                    try
                    {
                        object createParams = !string.IsNullOrEmpty(browserContextId)
                            ? new { url = req.Text, browserContextId }
                            : new { url = req.Text };
                        await SafeSendAsync(ws, new { id = reqId, method = "Target.createTarget", @params = createParams }, ct);
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                        using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(null));
                        var createdId = await tcs.Task;
                        if (!string.IsNullOrEmpty(createdId))
                        {
                            _ = SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Target.activateTarget", @params = new { targetId = createdId } }, CancellationToken.None);
                            return (true, createdId);
                        }
                    }
                    finally
                    {
                        _pendingTargetCreates.TryRemove(reqId, out _);
                    }
                }
                else if (action == "close_tab")
                {
                    var closeId = !string.IsNullOrWhiteSpace(req.PageId) ? req.PageId : fallbackTargetId;
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Target.closeTarget", @params = new { targetId = closeId } }, ct);
                    _hub.RetireTarget(_cdpUrl, closeId);
                    return (true, null);
                }
                else if (action == "activate_tab" && !string.IsNullOrWhiteSpace(req.PageId))
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Target.activateTarget", @params = new { targetId = req.PageId } }, ct);
                    return (true, req.PageId);
                }
            }
            catch { }
            return (false, null);
        }

        public async Task<bool> TryDispatchInputAsync(BrowserInputRequest req, CancellationToken ct)
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open) return false;
            var action = (req.Action ?? "click").Trim().ToLowerInvariant();
            if (action is "new_tab" or "close_tab") return false; // Tab lifecycle handled by endpoint

            try
            {
                var x = Math.Round(Math.Clamp(req.XRatio ?? 0.5, 0.0, 1.0) * _viewportW);
                var y = Math.Round(Math.Clamp(req.YRatio ?? 0.5, 0.0, 1.0) * _viewportH);

                if (action == "click")
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none" } }, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 } }, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 } }, ct);
                    ProbeScrollMetrics(ws, ct, force: true);
                    ProbeFocusedEditable(ws, (int)x, (int)y, ct);
                }
                else if (action == "dblclick")
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none" } }, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 2 } }, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 2 } }, ct);
                    ProbeFocusedEditable(ws, (int)x, (int)y, ct);
                }
                else if (action == "rightclick")
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none" } }, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "right", buttons = 2, clickCount = 1 } }, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "right", buttons = 0, clickCount = 1 } }, ct);
                }
                else if (action == "mousedown")
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none", buttons = 0 } }, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 } }, ct);
                    ProbeScrollMetrics(ws, ct, force: true);
                    ProbeFocusedEditable(ws, (int)x, (int)y, ct);
                }
                else if (action == "mousemove_drag")
                {
                    TriggerMotionSmoothBoost(ws, ct);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "left", buttons = 1 } }, ct);
                    ProbeScrollMetrics(ws, ct, force: false);
                }
                else if (action == "mouseup")
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 } }, ct);
                    ProbeScrollMetrics(ws, ct, force: true);
                }
                else if (action == "scroll_to")
                {
                    TriggerMotionSmoothBoost(ws, ct);
                    var ratio = Math.Clamp(req.YRatio ?? 0.0, 0.0, 1.0);
                    var ratioStr = ratio.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
                    var jsScrollTo = $$"""(() => { try { const r = {{ratioStr}}; let target = document.scrollingElement || document.documentElement || document.body; if (!target || target.scrollHeight <= target.clientHeight + 2) { let best = null, bestArea = 0; for (const el of document.querySelectorAll('*')) { if (el.scrollHeight > el.clientHeight + 8) { const cs = window.getComputedStyle(el); if (cs.overflowY === 'auto' || cs.overflowY === 'scroll' || cs.overflowY === 'overlay') { const rect = el.getBoundingClientRect(); const area = rect.width * rect.height; if (area > bestArea) { bestArea = area; best = el; } } } } if (best) target = best; } if (target && target.scrollHeight > target.clientHeight) { const nextTop = Math.round((target.scrollHeight - target.clientHeight) * r); if (target === document.scrollingElement || target === document.documentElement || target === document.body) { window.scrollTo({ top: nextTop, behavior: 'instant' }); } else { target.scrollTop = nextTop; } if (document.documentElement) { let pt = window.__ag360PaintTick; if (!pt || !document.contains(pt)) { pt = document.createElement('div'); pt.style.cssText = 'position:fixed;right:0;bottom:0;width:2px;height:2px;pointer-events:none;z-index:2147483647;opacity:0.015'; document.documentElement.appendChild(pt); window.__ag360PaintTick = pt; } pt.style.backgroundColor = (window.__ag360PaintN = ((window.__ag360PaintN || 0) + 1) & 1) ? '#010101' : '#fefefe'; } return { scrollTop: Math.round(target.scrollTop || window.scrollY || 0), scrollHeight: Math.round(target.scrollHeight || 0), clientHeight: Math.round(target.clientHeight || window.innerHeight || 0) }; } return null; } catch { return null; } })()""";
                    await SafeSendAsync(ws, new { id = 778, method = "Runtime.evaluate", @params = new { expression = jsScrollTo, returnByValue = true } }, ct);
                }
                else if (action == "move")
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseMoved", x, y, button = "none" } }, ct);
                    var nowTick = Environment.TickCount64;
                    if (nowTick - Interlocked.Read(ref _lastCursorProbeTick) >= 16)
                    {
                        Interlocked.Exchange(ref _lastCursorProbeTick, nowTick);
                        var ix = (int)x;
                        var iy = (int)y;
                        var js = $$"""(() => { try { let el = document.elementFromPoint({{ix}}, {{iy}}); if (!el) return 'default'; if (el.shadowRoot && typeof el.shadowRoot.elementFromPoint === 'function') { const inner = el.shadowRoot.elementFromPoint({{ix}}, {{iy}}); if (inner) el = inner; } if (el.matches?.(':disabled,[aria-disabled="true"]') || el.closest?.(':disabled,[aria-disabled="true"]')) return 'not-allowed'; for (let cur = el, d = 0; cur && d < 6; cur = cur.parentElement, d++) { const c = window.getComputedStyle(cur).cursor; if (c && c !== 'auto' && c !== 'default') { const m = c.match(/(pointer|text|vertical-text|grab|grabbing|move|not-allowed|no-drop|wait|progress|crosshair|help|zoom-in|zoom-out|col-resize|row-resize|ew-resize|ns-resize|nesw-resize|nwse-resize|n-resize|e-resize|s-resize|w-resize|ne-resize|nw-resize|se-resize|sw-resize|cell|copy|alias|context-menu|none|default)\s*$/i); return m ? m[1].toLowerCase() : c; } } if (el.closest?.('a[href],button,summary,select,label[for],area[href],[role="button"],[role="link"],[role="tab"],[role="menuitem"],[role="option"],[role="switch"],[role="checkbox"],[role="radio"],input[type="button"],input[type="submit"],input[type="reset"],input[type="checkbox"],input[type="radio"],input[type="file"],input[type="range"],[onclick],[tabindex]:not([tabindex="-1"])')) return 'pointer'; if (el.closest?.('input:not([type="button"]):not([type="submit"]):not([type="reset"]):not([type="checkbox"]):not([type="radio"]):not([type="file"]):not([type="range"]):not([type="color"]):not([type="image"]):not([type="hidden"]),textarea,[contenteditable="true"],[contenteditable=""],[contenteditable="plaintext-only"],[role="textbox"],[role="searchbox"],[role="combobox"]')) return 'text'; const rootCs = window.getComputedStyle(el); if (rootCs.userSelect !== 'none') { for (const node of el.childNodes) { if (node.nodeType === 3 && node.textContent && node.textContent.trim().length > 0) { const range = document.createRange(); range.selectNodeContents(node); const rects = range.getClientRects(); for (let i = 0; i < rects.length; i++) { const r = rects[i]; if ({{ix}} >= r.left - 2 && {{ix}} <= r.right + 2 && {{iy}} >= r.top - 2 && {{iy}} <= r.bottom + 2) return 'text'; } } } } return 'default'; } catch { return 'default'; } })()""";
                        _ = SafeSendAsync(ws, new { id = 777, method = "Runtime.evaluate", @params = new { expression = js, returnByValue = true } }, ct);
                    }
                }
                else if (action == "scroll")
                {
                    var dx = Math.Clamp(req.DeltaX ?? 0, -2400, 2400);
                    var dy = Math.Clamp(req.DeltaY ?? 0, -2400, 2400);
                    lock (_scrollLock)
                    {
                        _pendingScrollDx = Math.Clamp(_pendingScrollDx + dx, -4800, 4800);
                        _pendingScrollDy = Math.Clamp(_pendingScrollDy + dy, -4800, 4800);
                        _pendingScrollX = (int)x;
                        _pendingScrollY = (int)y;
                    }
                    await FlushPendingScrollIfIdleAsync(ws, ct);
                    TriggerMotionSmoothBoost(ws, ct);
                }
                else if (action == "ime_commit")
                {
                    var bsCount = Math.Clamp(req.Backspaces ?? 0, 0, 64);
                    for (int i = 0; i < bsCount; i++)
                    {
                        await SafeSendAsync(ws, new
                        {
                            id = Interlocked.Increment(ref _cmdId),
                            method = "Input.dispatchKeyEvent",
                            @params = new
                            {
                                type = "rawKeyDown",
                                key = "Backspace",
                                code = "Backspace",
                                windowsVirtualKeyCode = 8,
                                nativeVirtualKeyCode = 8,
                                commands = new[] { "DeleteBackward" }
                            }
                        }, ct);
                        await SafeSendAsync(ws, new
                        {
                            id = Interlocked.Increment(ref _cmdId),
                            method = "Input.dispatchKeyEvent",
                            @params = new
                            {
                                type = "keyUp",
                                key = "Backspace",
                                code = "Backspace",
                                windowsVirtualKeyCode = 8,
                                nativeVirtualKeyCode = 8
                            }
                        }, ct);
                    }
                    if (!string.IsNullOrEmpty(req.Text))
                    {
                        var normText = req.Text.Normalize(System.Text.NormalizationForm.FormC);
                        await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.insertText", @params = new { text = normText } }, ct);
                    }
                    TriggerMotionSmoothBoost(ws, ct);
                }
                else if (action == "type" && !string.IsNullOrEmpty(req.Text))
                {
                    var normText = req.Text.Normalize(System.Text.NormalizationForm.FormC);
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.insertText", @params = new { text = normText } }, ct);
                    TriggerMotionSmoothBoost(ws, ct);
                }
                else if (action == "key" && !string.IsNullOrEmpty(req.Key))
                {
                    var k = req.Key;
                    var mods = req.Modifiers ?? 0;
                    bool hasCtrlOrMeta = (mods & 2) != 0 || (mods & 4) != 0;
                    bool hasShift = (mods & 8) != 0;
                    bool isBackspace = k.Equals("Backspace", StringComparison.OrdinalIgnoreCase);
                    bool isDelete = k.Equals("Delete", StringComparison.OrdinalIgnoreCase);
                    bool isEnter = k.Equals("Enter", StringComparison.OrdinalIgnoreCase);
                    int keyCode = isEnter ? 13
                        : isBackspace ? 8
                        : k.Equals("Escape", StringComparison.OrdinalIgnoreCase) ? 27
                        : k.Equals("Tab", StringComparison.OrdinalIgnoreCase) ? 9
                        : k.Equals("ArrowLeft", StringComparison.OrdinalIgnoreCase) ? 37
                        : k.Equals("ArrowUp", StringComparison.OrdinalIgnoreCase) ? 38
                        : k.Equals("ArrowRight", StringComparison.OrdinalIgnoreCase) ? 39
                        : k.Equals("ArrowDown", StringComparison.OrdinalIgnoreCase) ? 40
                        : isDelete ? 46
                        : k.Equals("Home", StringComparison.OrdinalIgnoreCase) ? 36
                        : k.Equals("End", StringComparison.OrdinalIgnoreCase) ? 35
                        : k.Equals("PageUp", StringComparison.OrdinalIgnoreCase) ? 33
                        : k.Equals("PageDown", StringComparison.OrdinalIgnoreCase) ? 34
                        : k.Equals(" ", StringComparison.Ordinal) || k.Equals("Space", StringComparison.OrdinalIgnoreCase) ? 32
                        : (k.Length == 1 ? (int)char.ToUpperInvariant(k[0]) : 0);
                    var codeStr = k.Length == 1 && char.IsLetter(k[0]) ? $"Key{char.ToUpperInvariant(k[0])}"
                        : k.Length == 1 && char.IsDigit(k[0]) ? $"Digit{k[0]}"
                        : (k == " " ? "Space" : k);
                    bool isPrintable = k.Length == 1 && !char.IsControl(k[0]) && !hasCtrlOrMeta;
                    string? keyText = isEnter ? "\r" : (isPrintable ? k : null);
                    string downType = (isPrintable || isEnter) ? "keyDown" : "rawKeyDown";
                    string[]? commands = null;
                    if (isBackspace)
                    {
                        commands = new[] { hasCtrlOrMeta ? "DeleteWordBackward" : "DeleteBackward" };
                    }
                    else if (isDelete)
                    {
                        commands = new[] { hasCtrlOrMeta ? "DeleteWordForward" : "DeleteForward" };
                    }
                    else if (k.Equals("ArrowLeft", StringComparison.OrdinalIgnoreCase))
                    {
                        commands = new[] { hasCtrlOrMeta ? (hasShift ? "MoveWordLeftAndModifySelection" : "MoveWordLeft") : (hasShift ? "MoveLeftAndModifySelection" : "MoveLeft") };
                    }
                    else if (k.Equals("ArrowRight", StringComparison.OrdinalIgnoreCase))
                    {
                        commands = new[] { hasCtrlOrMeta ? (hasShift ? "MoveWordRightAndModifySelection" : "MoveWordRight") : (hasShift ? "MoveRightAndModifySelection" : "MoveRight") };
                    }
                    else if (k.Equals("ArrowUp", StringComparison.OrdinalIgnoreCase))
                    {
                        commands = new[] { hasShift ? "MoveUpAndModifySelection" : "MoveUp" };
                    }
                    else if (k.Equals("ArrowDown", StringComparison.OrdinalIgnoreCase))
                    {
                        commands = new[] { hasShift ? "MoveDownAndModifySelection" : "MoveDown" };
                    }
                    else if (k.Equals("Home", StringComparison.OrdinalIgnoreCase))
                    {
                        commands = new[] { hasShift ? "MoveToBeginningOfLineAndModifySelection" : "MoveToBeginningOfLine" };
                    }
                    else if (k.Equals("End", StringComparison.OrdinalIgnoreCase))
                    {
                        commands = new[] { hasShift ? "MoveToEndOfLineAndModifySelection" : "MoveToEndOfLine" };
                    }
                    else if (hasCtrlOrMeta && k.Length == 1)
                    {
                        var lower = char.ToLowerInvariant(k[0]);
                        commands = lower switch
                        {
                            'a' => new[] { "SelectAll" },
                            'c' => new[] { "Copy" },
                            'x' => new[] { "Cut" },
                            'v' => new[] { "Paste" },
                            'z' => new[] { hasShift ? "Redo" : "Undo" },
                            'y' => new[] { "Redo" },
                            _ => null
                        };
                    }

                    await SafeSendAsync(ws, new {
                        id = Interlocked.Increment(ref _cmdId),
                        method = "Input.dispatchKeyEvent",
                        @params = new {
                            type = downType,
                            key = k,
                            code = codeStr,
                            text = keyText,
                            unmodifiedText = keyText,
                            windowsVirtualKeyCode = keyCode,
                            nativeVirtualKeyCode = keyCode,
                            modifiers = mods,
                            commands
                        }
                    }, ct);

                    if (isPrintable)
                    {
                        await SafeSendAsync(ws, new {
                            id = Interlocked.Increment(ref _cmdId),
                            method = "Input.dispatchKeyEvent",
                            @params = new {
                                type = "char",
                                key = k,
                                code = codeStr,
                                text = keyText,
                                unmodifiedText = keyText,
                                windowsVirtualKeyCode = keyCode,
                                nativeVirtualKeyCode = keyCode,
                                modifiers = mods
                            }
                        }, ct);
                    }

                    await SafeSendAsync(ws, new {
                        id = Interlocked.Increment(ref _cmdId),
                        method = "Input.dispatchKeyEvent",
                        @params = new {
                            type = "keyUp",
                            key = k,
                            code = codeStr,
                            windowsVirtualKeyCode = keyCode,
                            nativeVirtualKeyCode = keyCode,
                            modifiers = mods
                        }
                    }, ct);

                    if (hasCtrlOrMeta && k.Length == 1 && (char.ToLowerInvariant(k[0]) == 'c' || char.ToLowerInvariant(k[0]) == 'x'))
                    {
                        var isCut = char.ToLowerInvariant(k[0]) == 'x' ? "true" : "false";
                        var jsCopy = $"(() => {{ try {{ const a = document.activeElement; let txt = ''; if (a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA')) {{ const s = a.selectionStart || 0, e = a.selectionEnd || 0; if (e > s) txt = a.value.substring(s, e); }} if (!txt) txt = window.getSelection()?.toString() || ''; try {{ document.execCommand({isCut} ? 'cut' : 'copy'); }} catch {{}} return txt; }} catch {{ return ''; }} }})()";
                        _ = SafeSendAsync(ws, new { id = 780, method = "Runtime.evaluate", @params = new { expression = jsCopy, returnByValue = true } }, ct);
                    }
                }
                else if (action == "clipboard_copy")
                {
                    // Copy focused/selected text from DOM and return via Runtime.evaluate
                    var jsCopy = "(() => { try { document.execCommand('copy'); const a = document.activeElement; if (a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA')) { const s = a.selectionStart || 0, e = a.selectionEnd || 0; if (e > s) return a.value.substring(s, e); } return window.getSelection()?.toString() || ''; } catch { return ''; } })()";
                    _ = SafeSendAsync(ws, new { id = 780, method = "Runtime.evaluate", @params = new { expression = jsCopy, returnByValue = true } }, ct);
                }
                else if (action == "clipboard_paste" && req.Text != null)
                {
                    // Clean paste text directly into active element or dispatch insertText
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.insertText", @params = new { text = req.Text } }, ct);
                }
                else if (action == "navigate" && !string.IsNullOrWhiteSpace(req.Text))
                {
                    var navUrl = req.Text.Trim();
                    if (!navUrl.Contains("://") && !navUrl.StartsWith("about:"))
                    {
                        navUrl = navUrl.Contains(".") && !navUrl.Contains(" ") ? $"https://{navUrl}" : $"https://www.google.com/search?q={Uri.EscapeDataString(navUrl)}";
                    }
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Page.navigate", @params = new { url = navUrl } }, ct);
                }
                else if (action == "activate_tab" && !string.IsNullOrWhiteSpace(req.PageId))
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Target.activateTarget", @params = new { targetId = req.PageId } }, ct);
                }
                else if (action == "reload")
                {
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Page.reload", @params = new { ignoreCache = false } }, ct);
                }
                else if (action == "back" || action == "forward")
                {
                    var js = action == "back" ? "window.history.back()" : "window.history.forward()";
                    await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Runtime.evaluate", @params = new { expression = js } }, ct);
                }
                else if (action == "find")
                {
                    var qText = (req.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(qText))
                    {
                        await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Runtime.evaluate", @params = new { expression = "try { window.getSelection()?.removeAllRanges(); } catch {}" } }, ct);
                    }
                    else
                    {
                        var backwards = string.Equals(req.Key, "prev", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                        var qJson = JsonSerializer.Serialize(qText);
                        var jsFind = $$"""(() => { try { const found = window.find({{qJson}}, false, {{backwards}}, true, false, true, false); const sel = window.getSelection(); if (sel && sel.rangeCount > 0) { const el = sel.anchorNode?.parentElement; if (el) el.scrollIntoView({ block: 'center', behavior: 'instant' }); } return found; } catch { return false; } })()""";
                        await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Runtime.evaluate", @params = new { expression = jsFind } }, ct);
                        ProbeScrollMetrics(ws, ct, force: true);
                    }
                }
                else if (action == "quality" && req.Width is > 0)
                {
                    await AdjustAdaptiveQualityAsync(req.Width.Value, ct);
                }
                else if (action == "resize" && req.Width is > 0 && req.Height is > 0)
                {
                    var wantMobile = req.IsMobile ?? string.Equals(req.DeviceType, "phone", StringComparison.OrdinalIgnoreCase);
                    var (w, h) = NormalizeViewport(req.Width.Value, req.Height.Value, _nativeSurfaceMaxW, _nativeSurfaceMaxH, wantMobile);
                    var nextScale = ComputeBackingScale(w, h, _nativeSurfaceMaxW, _nativeSurfaceMaxH, wantMobile);
                    if (_isMobileViewport == wantMobile && Math.Abs(_viewportW - w) <= 4 && Math.Abs(_viewportH - h) <= 4 && Math.Abs(_backingScale - nextScale) <= 0.005)
                    {
                        return true;
                    }
                    _viewportW = w;
                    _viewportH = h;
                    _backingScale = nextScale;
                    _isMobileViewport = wantMobile;
                    _hasTargetViewport = true;
                    LastFrame = null;
                    LastBinaryFrame = null;
                    LastFrameBase64 = null;
                    var capW = Math.Max(320, (int)Math.Round(w * nextScale));
                    var capH = Math.Max(240, (int)Math.Round(h * nextScale));
                    await SafeSendAsync(ws, new
                    {
                        id = Interlocked.Increment(ref _cmdId),
                        method = "Emulation.setDeviceMetricsOverride",
                        @params = new
                        {
                            width = w,
                            height = h,
                            deviceScaleFactor = 1,
                            scale = nextScale,
                            mobile = wantMobile,
                            screenWidth = w,
                            screenHeight = h
                        }
                    }, ct);
                    await RestartScreencastAsync(ws, capW, capH, ct);
                    if (Math.Abs(nextScale - 1.0) <= 0.01)
                    {
                        _ = SafeSendAsync(ws, new { id = 888, method = "Page.captureScreenshot", @params = new { format = "jpeg", quality = _currentQuality, fromSurface = true, optimizeForSpeed = true } }, ct);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task FlushPendingScrollIfIdleAsync(ClientWebSocket ws, CancellationToken ct)
        {
            if (ws.State != WebSocketState.Open || ct.IsCancellationRequested) return;
            var nowTick = Environment.TickCount64;
            if (Interlocked.CompareExchange(ref _scrollEvalInFlight, 1, 0) != 0)
            {
                // Watchdog: if a previous scroll eval took >120ms without ACK, release the lock
                if (nowTick - Interlocked.Read(ref _lastScrollMetricsTick) > 120)
                {
                    Interlocked.Exchange(ref _scrollEvalInFlight, 0);
                    if (Interlocked.CompareExchange(ref _scrollEvalInFlight, 1, 0) != 0) return;
                }
                else
                {
                    return;
                }
            }

            double dx, dy;
            int ix, iy;
            lock (_scrollLock)
            {
                dx = _pendingScrollDx;
                dy = _pendingScrollDy;
                ix = _pendingScrollX;
                iy = _pendingScrollY;
                _pendingScrollDx = 0;
                _pendingScrollDy = 0;
            }

            if (Math.Abs(dx) < 0.05 && Math.Abs(dy) < 0.05)
            {
                Interlocked.Exchange(ref _scrollEvalInFlight, 0);
                return;
            }

            Interlocked.Exchange(ref _lastScrollMetricsTick, nowTick);
            try
            {
                var dxStr = dx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                var dyStr = dy.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                // Hybrid O(1) DOM scroll + 1px native mouseWheel compositor wake (zero querySelectorAll overhead, 22-30fps on both standard and overflow:hidden SPA pages)
                var jsScrollBy = $$"""(() => { try { const dx = {{dxStr}}, dy = {{dyStr}}, ix = {{ix}}, iy = {{iy}}, now = Date.now(); const docEl = document.scrollingElement || document.documentElement || document.body; const instantScroll = (node, x, y) => { if (node === docEl || node === document.documentElement || node === document.body) { window.scrollBy({ left: x, top: y, behavior: 'instant' }); return; } if (typeof node.scrollBy === 'function') { try { node.scrollBy({ left: x, top: y, behavior: 'instant' }); return; } catch {} } node.scrollTop += y; node.scrollLeft += x; }; let target = null; const cached = window.__ag360ScrollEl; if (cached && (cached === docEl || document.contains(cached)) && Math.abs((window.__ag360ScrollX || 0) - ix) <= 64 && Math.abs((window.__ag360ScrollY || 0) - iy) <= 64 && (now - (window.__ag360ScrollAt || 0) < 1200)) { if ((cached.scrollHeight > cached.clientHeight + 2 && Math.abs(dy) > 0.05) || (cached.scrollWidth > cached.clientWidth + 2 && Math.abs(dx) > 0.05)) target = cached; } if (!target) { let el = document.elementFromPoint(ix, iy); while (el && el !== document.body && el !== document.documentElement) { if ((el.scrollHeight > el.clientHeight + 2 && Math.abs(dy) > 0.05) || (el.scrollWidth > el.clientWidth + 2 && Math.abs(dx) > 0.05)) { const cs = window.getComputedStyle(el); const canY = Math.abs(dy) > 0.05 && (cs.overflowY === 'auto' || cs.overflowY === 'scroll' || cs.overflowY === 'overlay') && el.scrollHeight > el.clientHeight + 2; const canX = Math.abs(dx) > 0.05 && (cs.overflowX === 'auto' || cs.overflowX === 'scroll' || cs.overflowX === 'overlay') && el.scrollWidth > el.clientWidth + 2; if (canY || canX) { target = el; break; } } el = el.parentElement; } } if (!target) { if (docEl && ((docEl.scrollHeight > docEl.clientHeight + 4 && Math.abs(dy) > 0.05) || (docEl.scrollWidth > docEl.clientWidth + 4 && Math.abs(dx) > 0.05))) { target = docEl; } else { let best = window.__ag360FallbackEl; if (!best || !document.contains(best) || best.scrollHeight <= best.clientHeight + 4) { best = null; let bestArea = 0; const gx = [0.5, 0.35, 0.65, 0.5, 0.5, 0.25, 0.75], gy = [0.5, 0.5, 0.5, 0.3, 0.7, 0.6, 0.6]; const vw = window.innerWidth || 800, vh = window.innerHeight || 600; for (let k = 0; k < gx.length; k++) { let node = document.elementFromPoint(Math.round(vw * gx[k]), Math.round(vh * gy[k])); while (node && node !== document.body && node !== document.documentElement) { if (node.scrollHeight > node.clientHeight + 8) { const cs = window.getComputedStyle(node); if (cs.overflowY === 'auto' || cs.overflowY === 'scroll' || cs.overflowY === 'overlay') { const area = node.clientWidth * node.clientHeight; if (area > bestArea) { bestArea = area; best = node; } break; } } node = node.parentElement; } } window.__ag360FallbackEl = best; } target = best || docEl; } } if (target) { instantScroll(target, dx, dy); window.__ag360ScrollEl = target; window.__ag360ScrollX = ix; window.__ag360ScrollY = iy; window.__ag360ScrollAt = now; } const mEl = target || docEl; return mEl ? { scrollTop: Math.round(mEl === docEl ? (window.scrollY || mEl.scrollTop || 0) : mEl.scrollTop), scrollHeight: Math.round(mEl.scrollHeight || 0), clientHeight: Math.round(mEl === docEl ? (window.innerHeight || mEl.clientHeight || 0) : mEl.clientHeight) } : null; } catch { return null; } })()""";
                await SafeSendAsync(ws, new { id = 779, method = "Runtime.evaluate", @params = new { expression = jsScrollBy, returnByValue = true } }, ct);
                var nudgeDx = Math.Abs(dx) > 0.2 ? (dx > 0 ? 1 : -1) : 0;
                var nudgeDy = Math.Abs(dy) > 0.2 ? (dy > 0 ? 1 : -1) : 0;
                _ = SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Input.dispatchMouseEvent", @params = new { type = "mouseWheel", x = ix, y = iy, deltaX = nudgeDx, deltaY = nudgeDy } }, ct);
            }
            catch
            {
                Interlocked.Exchange(ref _scrollEvalInFlight, 0);
            }
        }

        private void TriggerMotionSmoothBoost(ClientWebSocket ws, CancellationToken ct)
        {
            var now = Environment.TickCount64;
            Interlocked.Exchange(ref _activeMotionUntilTick, now + 320);
        }

        private async Task RestartScreencastAsync(ClientWebSocket ws, int width, int height, CancellationToken ct)
        {
            await SafeSendAsync(ws, new { id = Interlocked.Increment(ref _cmdId), method = "Page.stopScreencast", @params = new { } }, ct);
            await SafeSendAsync(ws, new
            {
                id = Interlocked.Increment(ref _cmdId),
                method = "Page.startScreencast",
                @params = new { format = "jpeg", quality = Volatile.Read(ref _currentQuality), maxWidth = width, maxHeight = height, everyNthFrame = 1 }
            }, ct);
        }

        private void PumpHighSpeedFrameIfNeeded(ClientWebSocket ws, CancellationToken ct, long nowTick)
        {
            if (ws.State != WebSocketState.Open || ct.IsCancellationRequested) return;
            if (Math.Abs(_backingScale - 1.0) > 0.01) return;
            if (nowTick > Interlocked.Read(ref _activeMotionUntilTick)) return;
            // Native VSync priority: when Page.startScreencast is actively delivering compositor frames (<24ms), avoid competing Page.captureScreenshot surface readbacks
            if (nowTick - Interlocked.Read(ref _lastScreencastFrameTick) < 24) return;
            if (nowTick - Interlocked.Read(ref _lastPumpedSentTick) < 14) return;
            if (Interlocked.CompareExchange(ref _inFlightPumpedCaptures, 1, 0) != 0)
            {
                if (nowTick - Interlocked.Read(ref _lastPumpedSentTick) > 140)
                {
                    Interlocked.Exchange(ref _inFlightPumpedCaptures, 0);
                    if (Interlocked.CompareExchange(ref _inFlightPumpedCaptures, 1, 0) != 0) return;
                }
                else
                {
                    return;
                }
            }
            Interlocked.Exchange(ref _lastPumpedSentTick, nowTick);
            var pumpQuality = Math.Clamp(Volatile.Read(ref _currentQuality) - 8, 50, 68);
            var pumpId = 100000 + (Interlocked.Increment(ref _cmdId) & 0xFFFFF);
            _ = SafeSendAsync(ws, new
            {
                id = pumpId,
                method = "Page.captureScreenshot",
                @params = new { format = "jpeg", quality = pumpQuality, fromSurface = true, optimizeForSpeed = true }
            }, ct);
        }

        private void ProbeScrollMetrics(ClientWebSocket ws, CancellationToken ct, bool force)
        {
            var nowTick = Environment.TickCount64;
            if (!force && nowTick - Interlocked.Read(ref _lastScrollMetricsTick) < 45) return;
            Interlocked.Exchange(ref _lastScrollMetricsTick, nowTick);
            const string jsMetrics = "(() => { try { const docEl = document.scrollingElement || document.documentElement || document.body; const cached = window.__ag360ScrollEl; const mEl = (cached && cached !== docEl && document.contains(cached) && cached.scrollHeight > cached.clientHeight + 4) ? cached : docEl; return mEl ? { scrollTop: Math.round(mEl === docEl ? (window.scrollY || mEl.scrollTop || 0) : mEl.scrollTop), scrollHeight: Math.round(mEl.scrollHeight || 0), clientHeight: Math.round(mEl === docEl ? (window.innerHeight || mEl.clientHeight || 0) : mEl.clientHeight) } : null; } catch { return null; } })()";
            _ = SafeSendAsync(ws, new { id = 778, method = "Runtime.evaluate", @params = new { expression = jsMetrics, returnByValue = true } }, ct);
        }

        private void BroadcastScrollMetrics(int scrollTop, int scrollHeight, int clientHeight)
        {
            if (scrollHeight <= 0 || clientHeight <= 0) return;
            var key = $"{scrollTop}:{scrollHeight}:{clientHeight}";
            if (string.Equals(key, _lastScrollJson, StringComparison.Ordinal)) return;
            _lastScrollJson = key;
            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                method = "Page.scrollMetrics",
                @params = new { scrollTop, scrollHeight, clientHeight }
            });
            lock (_viewers)
            {
                foreach (var (_, ctrlCh, _) in _viewers)
                {
                    ctrlCh.Writer.TryWrite(new StreamPacket(msgBytes, WebSocketMessageType.Text));
                }
            }
        }

        private void BroadcastClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                method = "Page.clipboardContent",
                @params = new { text }
            });
            lock (_viewers)
            {
                foreach (var (_, ctrlCh, _) in _viewers)
                {
                    ctrlCh.Writer.TryWrite(new StreamPacket(msgBytes, WebSocketMessageType.Text));
                }
            }
        }

        private void BroadcastCursorChanged(string cursorStyle)
        {
            if (string.IsNullOrWhiteSpace(cursorStyle)) return;
            var normalized = cursorStyle.Trim();
            if (string.Equals(normalized, _lastCursor, StringComparison.Ordinal)) return;
            _lastCursor = normalized;
            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                method = "Page.cursorChanged",
                @params = new { cursor = normalized }
            });
            lock (_viewers)
            {
                foreach (var (_, ctrlCh, _) in _viewers)
                {
                    ctrlCh.Writer.TryWrite(new StreamPacket(msgBytes, WebSocketMessageType.Text));
                }
            }
        }

        private void ProbeFocusedEditable(ClientWebSocket ws, int ix, int iy, CancellationToken ct)
        {
            var isMob = _isMobileViewport ? "true" : "false";
            var jsProbe = $$"""(() => { try { let el = document.activeElement; if (!el || el === document.body || el === document.documentElement) { el = document.elementFromPoint({{ix}}, {{iy}}); } if (!el) return { editable: false }; if (el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement; const editEl = el.closest?.('input:not([type="button"]):not([type="submit"]):not([type="reset"]):not([type="checkbox"]):not([type="radio"]):not([type="file"]):not([type="range"]):not([type="color"]):not([type="image"]):not([type="hidden"]),textarea,[contenteditable="true"],[contenteditable=""],[contenteditable="plaintext-only"],[role="textbox"],[role="searchbox"],[role="combobox"]'); if (!editEl) return { editable: false }; if ({{isMob}}) { const r0 = editEl.getBoundingClientRect(); const vh0 = window.innerHeight || document.documentElement.clientHeight || 600; if (r0.bottom > vh0 * 0.52 || r0.top < 36) { try { editEl.scrollIntoView({ block: 'center', behavior: 'instant' }); } catch {} } } const r = editEl.getBoundingClientRect(); const vh = Math.max(1, window.innerHeight || document.documentElement.clientHeight || 600); return { editable: true, inputType: (editEl.getAttribute?.('type') || editEl.tagName || 'text').toLowerCase(), yRatio: Math.round(((r.top + r.height / 2) / vh) * 1000) / 1000, bottomRatio: Math.round((r.bottom / vh) * 1000) / 1000 }; } catch { return { editable: false }; } })()""";
            _ = SafeSendAsync(ws, new { id = 781, method = "Runtime.evaluate", @params = new { expression = jsProbe, returnByValue = true } }, ct);
        }

        private void BroadcastEditableFocus(bool editable, string inputType, double yRatio, double bottomRatio)
        {
            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                method = "Page.inputFocusChanged",
                @params = new { editable, inputType, yRatio, bottomRatio }
            });
            lock (_viewers)
            {
                foreach (var (_, ctrlCh, _) in _viewers)
                {
                    ctrlCh.Writer.TryWrite(new StreamPacket(msgBytes, WebSocketMessageType.Text));
                }
            }
        }

        private bool Broadcast(string base64Jpeg, bool forcePacing = false)
        {
            if (string.IsNullOrEmpty(base64Jpeg)) return true;
            var now = Environment.TickCount64;
            if (string.Equals(base64Jpeg, LastFrameBase64, StringComparison.Ordinal) && (LastBinaryFrame is not null || LastFrame is not null))
            {
                return true;
            }
            byte[] binBytes;
            try { binBytes = Convert.FromBase64String(base64Jpeg); } catch { return true; }
            if (binBytes.Length == 0) return true;
            if (!MatchesTargetFrameDimensions(binBytes))
            {
                return false;
            }
            Interlocked.Exchange(ref _lastBroadcastTick, now);
            LastFrameBase64 = base64Jpeg;
            LastBinaryFrame = binBytes;
            byte[]? txtBytes = null;
            lock (_viewers)
            {
                foreach (var (frameCh, _, isBin) in _viewers)
                {
                    if (isBin)
                    {
                        if (binBytes.Length > 0)
                        {
                            frameCh.Writer.TryWrite(new StreamPacket(binBytes, WebSocketMessageType.Binary));
                        }
                    }
                    else
                    {
                        txtBytes ??= Frame(base64Jpeg);
                        LastFrame = txtBytes;
                        frameCh.Writer.TryWrite(new StreamPacket(txtBytes, WebSocketMessageType.Text));
                    }
                }
            }
            return true;
        }

        private async Task RunAsync(string cdpUrl, string targetId, int maxWidth, int quality, int initialWidth, int initialHeight, bool isMobileDevice = false)
        {
            var ct = _cts.Token;
            try
            {
                using var ws = new ClientWebSocket();
                _ws = ws;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                var cdp = new Uri(cdpUrl);
                await ws.ConnectAsync(new Uri($"ws://{cdp.Authority}/devtools/page/{Uri.EscapeDataString(targetId)}"), ct);
                var capDim = Math.Clamp(maxWidth, 320, 1920);
                if (initialWidth >= 320 && initialHeight >= 240)
                {
                    await SafeSendAsync(ws, new { id = 10, method = "Emulation.setFocusEmulationEnabled", @params = new { enabled = true } }, ct);
                    await SafeSendAsync(ws, new { id = 11, method = "Page.setWebLifecycleState", @params = new { state = "active" } }, ct);
                    await SafeSendAsync(ws, new { id = 14, method = "Page.bringToFront" }, ct);
                    await SafeSendAsync(ws, new { id = 12, method = "Emulation.clearDeviceMetricsOverride" }, ct);
                    await SafeSendAsync(ws, new { id = 15, method = "Runtime.evaluate", @params = new { expression = "JSON.stringify({w: window.innerWidth || document.documentElement.clientWidth || 1440, h: window.innerHeight || document.documentElement.clientHeight || 813})", returnByValue = true } }, ct);
                    var initBuf = new byte[16 * 1024];
                    using var initMs = new MemoryStream();
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        initMs.SetLength(0);
                        WebSocketReceiveResult ir;
                        do
                        {
                            ir = await ws.ReceiveAsync(initBuf, ct);
                            if (ir.MessageType == WebSocketMessageType.Close) return;
                            initMs.Write(initBuf, 0, ir.Count);
                        } while (!ir.EndOfMessage);
                        using var initDoc = JsonDocument.Parse(initMs.GetBuffer().AsMemory(0, (int)initMs.Length));
                        if (initDoc.RootElement.TryGetProperty("id", out var probeId) && probeId.GetInt32() == 15)
                        {
                            try
                            {
                                if (initDoc.RootElement.TryGetProperty("result", out var rProp) &&
                                    rProp.TryGetProperty("result", out var innerR) &&
                                    innerR.TryGetProperty("value", out var valProp) &&
                                    valProp.ValueKind == JsonValueKind.String)
                                {
                                    using var surfDoc = JsonDocument.Parse(valProp.GetString() ?? "{}");
                                    var sw = surfDoc.RootElement.TryGetProperty("w", out var swP) && swP.TryGetInt32(out var swV) ? swV : 0;
                                    var sh = surfDoc.RootElement.TryGetProperty("h", out var shP) && shP.TryGetInt32(out var shV) ? shV : 0;
                                    if (sw >= 640 && sh >= 480)
                                    {
                                        _nativeSurfaceMaxW = Math.Clamp(sw, 640, 1920);
                                        _nativeSurfaceMaxH = Math.Clamp(sh, 480, 1080);
                                    }
                                }
                            }
                            catch { }
                            break;
                        }
                    }
                    // Self-heal unmaximized Wayland/X11 Chromium window surfaces (<1300px wide):
                    // toggle normal (1440x900) -> maximized before applying Emulation.setDeviceMetricsOverride so the tab always renders at full 1408x771+ resolution!
                    if (_nativeSurfaceMaxW < 1300)
                    {
                        try
                        {
                            await SafeSendAsync(ws, new { id = 16, method = "Browser.getWindowForTarget", @params = new { targetId } }, ct);
                            int winId = 0;
                            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                            {
                                initMs.SetLength(0);
                                WebSocketReceiveResult ir;
                                do
                                {
                                    ir = await ws.ReceiveAsync(initBuf, ct);
                                    if (ir.MessageType == WebSocketMessageType.Close) return;
                                    initMs.Write(initBuf, 0, ir.Count);
                                } while (!ir.EndOfMessage);
                                using var winDoc = JsonDocument.Parse(initMs.GetBuffer().AsMemory(0, (int)initMs.Length));
                                if (winDoc.RootElement.TryGetProperty("id", out var widAck) && widAck.GetInt32() == 16)
                                {
                                    if (winDoc.RootElement.TryGetProperty("result", out var wRes) &&
                                        wRes.TryGetProperty("windowId", out var widProp) &&
                                        widProp.TryGetInt32(out var parsedWid))
                                    {
                                        winId = parsedWid;
                                    }
                                    break;
                                }
                            }
                            if (winId > 0)
                            {
                                await SafeSendAsync(ws, new { id = 17, method = "Browser.setWindowBounds", @params = new { windowId = winId, bounds = new { left = 0, top = 0, width = 1440, height = 900, windowState = "normal" } } }, ct);
                                await SafeSendAsync(ws, new { id = 18, method = "Browser.setWindowBounds", @params = new { windowId = winId, bounds = new { windowState = "maximized" } } }, ct);
                                await Task.Delay(80, ct);
                                await SafeSendAsync(ws, new { id = 19, method = "Runtime.evaluate", @params = new { expression = "JSON.stringify({w: window.innerWidth || document.documentElement.clientWidth || 1440, h: window.innerHeight || document.documentElement.clientHeight || 813})", returnByValue = true } }, ct);
                                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                                {
                                    initMs.SetLength(0);
                                    WebSocketReceiveResult ir;
                                    do
                                    {
                                        ir = await ws.ReceiveAsync(initBuf, ct);
                                        if (ir.MessageType == WebSocketMessageType.Close) return;
                                        initMs.Write(initBuf, 0, ir.Count);
                                    } while (!ir.EndOfMessage);
                                    using var reDoc = JsonDocument.Parse(initMs.GetBuffer().AsMemory(0, (int)initMs.Length));
                                    if (reDoc.RootElement.TryGetProperty("id", out var reId) && reId.GetInt32() == 19)
                                    {
                                        if (reDoc.RootElement.TryGetProperty("result", out var rProp) &&
                                            rProp.TryGetProperty("result", out var innerR) &&
                                            innerR.TryGetProperty("value", out var valProp) &&
                                            valProp.ValueKind == JsonValueKind.String)
                                        {
                                            using var surfDoc = JsonDocument.Parse(valProp.GetString() ?? "{}");
                                            var sw = surfDoc.RootElement.TryGetProperty("w", out var swP) && swP.TryGetInt32(out var swV) ? swV : 0;
                                            var sh = surfDoc.RootElement.TryGetProperty("h", out var shP) && shP.TryGetInt32(out var shV) ? shV : 0;
                                            if (sw >= 640 && sh >= 480)
                                            {
                                                _nativeSurfaceMaxW = Math.Clamp(sw, 640, 1920);
                                                _nativeSurfaceMaxH = Math.Clamp(sh, 480, 1080);
                                            }
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    var (w, h) = NormalizeViewport(initialWidth, initialHeight, _nativeSurfaceMaxW, _nativeSurfaceMaxH, _isMobileViewport);
                    var initScale = ComputeBackingScale(w, h, _nativeSurfaceMaxW, _nativeSurfaceMaxH, _isMobileViewport);
                    _viewportW = w;
                    _viewportH = h;
                    _backingScale = initScale;
                    var capW = Math.Max(320, (int)Math.Round(w * initScale));
                    var capH = Math.Max(240, (int)Math.Round(h * initScale));
                    await SafeSendAsync(ws, new
                    {
                        id = 13,
                        method = "Emulation.setDeviceMetricsOverride",
                        @params = new
                        {
                            width = w,
                            height = h,
                            deviceScaleFactor = 1,
                            scale = initScale,
                            mobile = _isMobileViewport,
                            screenWidth = w,
                            screenHeight = h
                        }
                    }, ct);
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        initMs.SetLength(0);
                        WebSocketReceiveResult ir;
                        do
                        {
                            ir = await ws.ReceiveAsync(initBuf, ct);
                            if (ir.MessageType == WebSocketMessageType.Close) return;
                            initMs.Write(initBuf, 0, ir.Count);
                        } while (!ir.EndOfMessage);
                        using var initDoc = JsonDocument.Parse(initMs.GetBuffer().AsMemory(0, (int)initMs.Length));
                        if (initDoc.RootElement.TryGetProperty("id", out var ackId) && ackId.GetInt32() == 13)
                        {
                            break;
                        }
                    }
                    await SafeSendAsync(ws, new { id = 1, method = "Page.startScreencast", @params = new { format = "jpeg", quality = 76, maxWidth = capW, maxHeight = capH, everyNthFrame = 1 } }, ct);
                    if (Math.Abs(initScale - 1.0) <= 0.01)
                    {
                        _ = SafeSendAsync(ws, new { id = 888, method = "Page.captureScreenshot", @params = new { format = "jpeg", quality = _currentQuality, fromSurface = true, optimizeForSpeed = true } }, ct);
                    }
                }
                else
                {
                    await SafeSendAsync(ws, new { id = 1, method = "Page.startScreencast", @params = new { format = "jpeg", quality = _currentQuality, maxWidth = capDim, maxHeight = capDim, everyNthFrame = 1 } }, ct);
                }



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
                    if (doc.RootElement.TryGetProperty("id", out var respId))
                    {
                        var rId = respId.GetInt32();
                        if (_pendingTargetCreates.TryRemove(rId, out var pendingTcs))
                        {
                            string? tid = null;
                            if (doc.RootElement.TryGetProperty("result", out var cRes) &&
                                cRes.TryGetProperty("targetId", out var tProp))
                            {
                                tid = tProp.GetString();
                            }
                            pendingTcs.TrySetResult(tid);
                            continue;
                        }
                        if (rId == 777 &&
                            doc.RootElement.TryGetProperty("result", out var curRes) &&
                            curRes.TryGetProperty("result", out var innerCur) &&
                            innerCur.TryGetProperty("value", out var curVal) &&
                            curVal.ValueKind == JsonValueKind.String)
                        {
                            var detected = curVal.GetString();
                            if (!string.IsNullOrEmpty(detected))
                            {
                                BroadcastCursorChanged(detected);
                            }
                            continue;
                        }
                        if (rId == 780 &&
                            doc.RootElement.TryGetProperty("result", out var clipRes) &&
                            clipRes.TryGetProperty("result", out var innerClip) &&
                            innerClip.TryGetProperty("value", out var clipVal) &&
                            clipVal.ValueKind == JsonValueKind.String)
                        {
                            var copiedText = clipVal.GetString();
                            if (!string.IsNullOrEmpty(copiedText))
                            {
                                BroadcastClipboardText(copiedText);
                            }
                            continue;
                        }
                        if (rId == 781 &&
                            doc.RootElement.TryGetProperty("result", out var edRes) &&
                            edRes.TryGetProperty("result", out var innerEd) &&
                            innerEd.TryGetProperty("value", out var edVal) &&
                            edVal.ValueKind == JsonValueKind.Object)
                        {
                            var isEd = edVal.TryGetProperty("editable", out var edP) && edP.ValueKind == JsonValueKind.True;
                            var inType = edVal.TryGetProperty("inputType", out var itP) && itP.ValueKind == JsonValueKind.String ? itP.GetString() ?? "text" : "text";
                            var yR = edVal.TryGetProperty("yRatio", out var yrP) && yrP.TryGetDouble(out var yrV) ? yrV : 0.5;
                            var bR = edVal.TryGetProperty("bottomRatio", out var brP) && brP.TryGetDouble(out var brV) ? brV : 0.5;
                            BroadcastEditableFocus(isEd, inType, yR, bR);
                            if (isEd && _isMobileViewport)
                            {
                                ProbeScrollMetrics(ws, ct, force: true);
                                TriggerMotionSmoothBoost(ws, ct);
                            }
                            continue;
                        }
                        if (rId == 778 || rId == 779)
                        {
                            try
                            {
                                if (doc.RootElement.TryGetProperty("result", out var scrRes) &&
                                    scrRes.TryGetProperty("result", out var innerScr) &&
                                    innerScr.TryGetProperty("value", out var scrVal) &&
                                    scrVal.ValueKind == JsonValueKind.Object)
                                {
                                    var st = scrVal.TryGetProperty("scrollTop", out var stP) && stP.TryGetInt32(out var stV) ? stV : 0;
                                    var sh = scrVal.TryGetProperty("scrollHeight", out var shP) && shP.TryGetInt32(out var shV) ? shV : 0;
                                    var ch = scrVal.TryGetProperty("clientHeight", out var chP) && chP.TryGetInt32(out var chV) ? chV : 0;
                                    BroadcastScrollMetrics(st, sh, ch);
                                }
                            }
                            finally
                            {
                                if (rId == 779)
                                {
                                    Interlocked.Exchange(ref _scrollEvalInFlight, 0);
                                    _ = FlushPendingScrollIfIdleAsync(ws, ct);
                                }
                            }
                            continue;
                        }
                        if (rId == 888 || rId >= 100000)
                        {
                            if (rId >= 100000)
                            {
                                Interlocked.Exchange(ref _inFlightPumpedCaptures, 0);
                            }
                            if (doc.RootElement.TryGetProperty("result", out var snapRes) &&
                                snapRes.TryGetProperty("data", out var snapDataProp))
                            {
                                if (rId >= 100000)
                                {
                                    var prevMax = Volatile.Read(ref _latestPumpedRespId);
                                    if (rId < prevMax) continue;
                                    Volatile.Write(ref _latestPumpedRespId, rId);
                                    var now = Environment.TickCount64;
                                    if (now <= Interlocked.Read(ref _activeMotionUntilTick))
                                    {
                                        PumpHighSpeedFrameIfNeeded(ws, ct, now);
                                    }
                                }
                                var snapB64 = snapDataProp.GetString();
                                if (!string.IsNullOrEmpty(snapB64))
                                {
                                    Broadcast(snapB64);
                                }
                            }
                            continue;
                        }
                        if (rId == 12 &&
                            doc.RootElement.TryGetProperty("result", out var res) &&
                            res.TryGetProperty("cssVisualViewport", out var vp))
                        {
                            if (vp.TryGetProperty("clientWidth", out var cw) && cw.GetDouble() > 0) _viewportW = cw.GetDouble();
                            if (vp.TryGetProperty("clientHeight", out var ch) && ch.GetDouble() > 0) _viewportH = ch.GetDouble();
                            continue;
                        }
                    }


                    if (!doc.RootElement.TryGetProperty("method", out var m) || m.GetString() != "Page.screencastFrame") continue;
                    var p = doc.RootElement.GetProperty("params");
                    if (_hasTargetViewport && p.TryGetProperty("metadata", out var meta))
                    {
                        var dw = meta.TryGetProperty("deviceWidth", out var dwProp) ? dwProp.GetDouble() : _viewportW;
                        var dh = meta.TryGetProperty("deviceHeight", out var dhProp) ? dhProp.GetDouble() : _viewportH;
                        var expectedBackingW = _viewportW * _backingScale;
                        var expectedBackingH = _viewportH * _backingScale;
                        var matchesLogicalMeta = Math.Abs(dw - _viewportW) <= 20 && Math.Abs(dh - _viewportH) <= 20;
                        var matchesBackingMeta = Math.Abs(dw - expectedBackingW) <= 20 && Math.Abs(dh - expectedBackingH) <= 20;
                        if (!matchesLogicalMeta && !matchesBackingMeta)
                        {
                            var sidDrop = p.GetProperty("sessionId").GetInt32();
                            _ = SafeSendAsync(ws, new { id = 2, method = "Page.screencastFrameAck", @params = new { sessionId = sidDrop } }, ct);
                            var nowHeal = Environment.TickCount64;
                            if (nowHeal - Interlocked.Read(ref _lastMetricsHealTick) >= 1200)
                            {
                                Interlocked.Exchange(ref _lastMetricsHealTick, nowHeal);
                                var w = (int)Math.Round(_viewportW);
                                var h = (int)Math.Round(_viewportH);
                                var healScale = ComputeBackingScale(w, h, _nativeSurfaceMaxW, _nativeSurfaceMaxH, _isMobileViewport);
                                _backingScale = healScale;
                                var capW = Math.Max(320, (int)Math.Round(w * healScale));
                                var capH = Math.Max(240, (int)Math.Round(h * healScale));
                                await SafeSendAsync(ws, new
                                {
                                    id = Interlocked.Increment(ref _cmdId),
                                    method = "Emulation.setDeviceMetricsOverride",
                                    @params = new
                                    {
                                        width = w,
                                        height = h,
                                        deviceScaleFactor = 1,
                                        scale = healScale,
                                        mobile = _isMobileViewport,
                                        screenWidth = w,
                                        screenHeight = h
                                    }
                                }, ct);
                                await RestartScreencastAsync(ws, capW, capH, ct);
                            }
                            continue;
                        }
                    }
                    else if (p.TryGetProperty("metadata", out var metaFallback))
                    {
                        if (metaFallback.TryGetProperty("deviceWidth", out var dw) && dw.GetDouble() > 0) _viewportW = dw.GetDouble();
                        if (metaFallback.TryGetProperty("deviceHeight", out var dh) && dh.GetDouble() > 0) _viewportH = dh.GetDouble();
                    }
                    // ACK immediately BEFORE Base64 conversion & broadcast so Chromium starts encoding the next 60fps frame with 0ms delay!
                    var frameSessionId = p.GetProperty("sessionId").GetInt32();
                    _ = SafeSendAsync(ws, new { id = 2, method = "Page.screencastFrameAck", @params = new { sessionId = frameSessionId } }, ct);
                    Interlocked.Exchange(ref _lastScreencastFrameTick, Environment.TickCount64);
                    var data = p.GetProperty("data").GetString() ?? "";
                    _ = Broadcast(data);
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastScrollMetricsTick) >= 240)
                    {
                        ProbeScrollMetrics(ws, ct, force: false);
                    }
                    
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
            {
                if (!ct.IsCancellationRequested) _logger.LogInformation("Screencast session ended: {Error}", ex.Message);
            }
            finally
            {
                _ws = null;
                lock (_viewers)
                {
                    foreach (var (fCh, cCh, _) in _viewers)
                    {
                        fCh.Writer.TryComplete();
                        cCh.Writer.TryComplete();
                    }
                }
                lock (_hub._gate)
                {
                    _hub._sessions.TryRemove(new KeyValuePair<string, Session>(Key, this));
                }
            }
        }

        private static readonly JsonSerializerOptions CdpJsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private async Task SafeSendAsync(WebSocket ws, object message, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, CdpJsonOptions), WebSocketMessageType.Text, true, ct);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
