using System.Text.Json.Nodes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>
/// Agent-scoped browser surface. Every call is confined to the agent's own Chromium profile
/// (browser context): pages, screenshots and the screencast never show another agent's tabs.
/// </summary>
public sealed class AgentBrowserCdpEndpoint : IEndpoint
{
    public const string RateLimitPolicy = "agent360-browser";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // Browser identity for the viewer: Selkies URL, CDP endpoint, owned profile, live tabs.
        routeBuilder.MapGet(
            "agents/{agentId}/browser",
            async Task<IResult> (string agentId, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                var info = await directory.GetAsync(agentId, ct);
                return info is null
                    ? TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser profile" })
                    : TypedResults.Ok(info);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowser")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Agent's browser service, owned Chromium profile and its live tabs")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        // Value-free health for monitoring (no tabs, URLs or endpoints): stays public while the rest of
        // /browser requires sign-in (SensitiveReadGuardMiddleware).
        routeBuilder.MapGet(
            "agents/{agentId}/browser/health",
            async Task<IResult> (string agentId, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                var info = await directory.GetAsync(agentId, ct);
                return info is null
                    ? TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser profile" })
                    : TypedResults.Ok(new { agentId, info.Service, info.Profile, info.CdpReachable, info.ProfileWindowOpen });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserHealth")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Whether the agent's browser and profile window are up (no tab data)")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        // Bring the agent's own profile window to the front of the shared Selkies desktop
        // before the viewer opens it, so the user sees this agent's tabs, not the last agent's.
        routeBuilder.MapPost(
            "agents/{agentId}/browser/focus",
            async Task<IResult> (string agentId, AgentBrowserDirectory directory, IHttpClientFactory httpFactory, CancellationToken ct) =>
            {
                var (page, cdp, contextId, error) = await PickPageAsync(directory, agentId, null, ct);
                if (page is null)
                {
                    return TypedResults.Conflict(new { message = error });
                }
                var ok = await ActivateAsync(httpFactory, cdp!, page.TargetId, contextId, ct);
                return ok
                    ? TypedResults.Ok(new { agentId, targetId = page.TargetId, url = page.Url })
                    : TypedResults.Problem("Failed to focus the agent's browser window", statusCode: StatusCodes.Status502BadGateway);
            })
            .MapToApiVersion(ApiVersion.Default)
            // Cross-site pages must not raise windows on the shared desktop (CSRF-style abuse).
            .AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                var origin = http.Request.Headers.Origin.FirstOrDefault();
                var cors = http.RequestServices.GetRequiredService<IOptions<CorsOptions>>().Value;
                if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(http, cors.AllowedOrigins))
                {
                    return Results.Problem("Origin not allowed", statusCode: StatusCodes.Status403Forbidden);
                }
                return await next(ctx);
            })
            .WithName("FocusAgentBrowser")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Bring the agent's own Chromium profile window to the front of the desktop")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/pages",
            async Task<IResult> (string agentId, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                var info = await directory.GetAsync(agentId, ct);
                if (info is null)
                {
                    return TypedResults.NotFound(new { message = $"Agent '{agentId}' has no mapped browser profile" });
                }
                return TypedResults.Ok(info.Tabs.Where(t => !t.Anchor)
                    .Select(t => new BrowserPageSummary(t.TargetId, t.Title, t.Url, "page", null)).ToList());
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserPages")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List the agent's own browser tabs (its Chromium profile only)")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/screenshot",
            async Task<IResult> (string agentId, [FromQuery] string? pageId, AgentBrowserDirectory directory, AgentBrowserGate gate, IMemoryCache cache, CancellationToken ct) =>
            {
                var (page, cdp, _, error) = await PickPageAsync(directory, agentId, pageId, ct);
                if (page is null)
                {
                    return TypedResults.NotFound(new { message = error });
                }
                var data = await CachedScreenshotAsync(gate, cache, cdp!, page.TargetId);
                return data is null
                    ? TypedResults.Problem("Failed to capture screenshot from CDP target.", statusCode: StatusCodes.Status502BadGateway)
                    : TypedResults.Ok(new BrowserScreenshotResponse(agentId, page.TargetId, "jpeg", data, DateTimeOffset.UtcNow));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserScreenshot")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Capture a screenshot of the agent's own browser tab")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/image.jpg",
            async Task<IResult> (string agentId, [FromQuery] string? pageId, AgentBrowserDirectory directory, AgentBrowserGate gate, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
            {
                var (page, cdp, _, error) = await PickPageAsync(directory, agentId, pageId, ct);
                if (page is null)
                {
                    return TypedResults.NotFound(new { message = error });
                }
                http.Response.Headers.CacheControl = "no-store";
                var key = $"agent360:browser-shot:{page.TargetId}";
                var data = await CachedScreenshotAsync(gate, cache, cdp!, page.TargetId);
                if (data is not null)
                {
                    cache.Set(key, data, TimeSpan.FromMinutes(5));
                }
                else if (cache.TryGetValue(key, out string? last) && last is not null)
                {
                    // A busy or background-throttled tab is common; a slightly stale frame beats a broken preview.
                    http.Response.Headers["X-Agent360-Stale"] = "1";
                    data = last;
                }
                else if (http.RequestServices.GetService<ScreencastHub>()?.LatestFrame(cdp!, page.TargetId) is { } live)
                {
                    http.Response.Headers["X-Agent360-Stale"] = "1";
                    data = live;
                }
                return data is null
                    ? TypedResults.Problem("Failed to capture screenshot", statusCode: StatusCodes.Status503ServiceUnavailable)
                    : TypedResults.File(Convert.FromBase64String(data), "image/jpeg");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserRawImage")
            .WithTags(EndpointTags.Agents)
            .WithSummary("JPEG of the agent's own browser tab")
            .RequireRateLimiting(RateLimitPolicy)
            .AllowAnonymous();

        // Live screencast of the agent's tab (inspection only; interactive control is the Selkies viewer).
        routeBuilder.Map(
            "agents/{agentId}/browser/stream",
            async (string agentId, HttpContext context, AgentBrowserDirectory directory, AgentBrowserGate gate, IOptions<CorsOptions> cors) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("WebSocket connection expected.");
                    return;
                }
                // Browsers always send Origin on a WebSocket handshake; only Agent360's own UI may watch.
                if (!IsAllowedOrigin(context, cors.Value.AllowedOrigins))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                var ct = context.RequestAborted;
                var (page, cdp, _, error) = await PickPageAsync(directory, agentId, context.Request.Query["pageId"], ct);
                if (page is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { message = error }, ct);
                    return;
                }
                var info = await directory.GetAsync(agentId, ct);
                var slots = gate.StreamSlots(info!.Instance);
                if (!await slots.WaitAsync(0, ct))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.Response.WriteAsJsonAsync(new { message = "Too many live viewers on this browser; use the preview." }, ct);
                    return;
                }
                try
                {
                    using var clientWs = await context.WebSockets.AcceptWebSocketAsync();
                    // Fixed size/quality buckets so concurrent viewers of one tab share a single CDP screencast.
                    var maxWidth = int.TryParse(context.Request.Query["maxWidth"], out var w) && w <= 800 ? 640 : 1280;
                    var quality = 55;
                    // Screencast only emits on repaint; an idle tab would leave a new viewer black until it changes.
                    var seed = await CachedScreenshotAsync(gate, context.RequestServices.GetRequiredService<IMemoryCache>(), cdp!, page.TargetId);
                    var hub = context.RequestServices.GetRequiredService<ScreencastHub>();
                    await hub.WatchAsync(cdp!, page.TargetId, maxWidth, quality, clientWs, seed, ct);
                }
                finally
                {
                    slots.Release();
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("StreamAgentBrowser")
            .WithTags(EndpointTags.Agents)
            .ExcludeFromDescription()
            .RequireRateLimiting(RateLimitPolicy);
    }

    /// <summary>The tab worth showing by default: a real web page first, never a chrome:// internal page
    /// (chrome://newtab renders out-of-process and Page.captureScreenshot hangs on it), else the profile anchor.</summary>
    internal static AgentBrowserTab? DefaultTab(IReadOnlyList<AgentBrowserTab> tabs) =>
        tabs.LastOrDefault(t => !t.Anchor && IsCapturable(t.Url))
        ?? tabs.FirstOrDefault(t => !t.Anchor && IsCapturable(t.Url))
        ?? tabs.FirstOrDefault(t => t.Anchor)
        ?? tabs.LastOrDefault(t => IsCapturable(t.Url));

    internal static bool IsCapturable(string url) =>
        !url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("chrome-untrusted://", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAllowedOrigin(HttpContext context, IReadOnlyCollection<string> allowed)
    {
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }
        if (allowed.Any(a => string.Equals(a.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        // Same-origin (UI and API behind one host via the frontend nginx /api proxy)
        // or local development loopback (e.g. Vite dev server on localhost:3000 or 127.0.0.1:3000).
        if (Uri.TryCreate(origin, UriKind.Absolute, out var o))
        {
            if (string.Equals(o.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (o.IsLoopback || o.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>One CDP capture per target per second, shared by every concurrent viewer.</summary>
    private static async Task<string?> CachedScreenshotAsync(AgentBrowserGate gate, IMemoryCache cache, string cdpUrl, string targetId)
    {
        var key = $"agent360:browser:shot:{cdpUrl}:{targetId}";
        if (cache.TryGetValue(key, out string? hit) && hit is not null)
        {
            return hit;
        }
        var data = await gate.Coalesce(key, async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                return await CaptureScreenshotAsync(cdpUrl, targetId, cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or HttpRequestException)
            {
                return null;
            }
        });
        if (data is not null)
        {
            cache.Set(key, data, TimeSpan.FromSeconds(1));
        }
        return data;
    }

    private static async Task<(AgentBrowserTab? Page, string? Cdp, string? ContextId, string? Error)> PickPageAsync(
        AgentBrowserDirectory directory, string agentId, string? pageId, CancellationToken ct)
    {
        var info = await directory.GetAsync(agentId, ct);
        if (info is null)
        {
            return (null, null, null, $"Agent '{agentId}' has no mapped browser profile");
        }
        if (!info.CdpReachable)
        {
            return (null, null, null, $"Browser {info.Service} is not reachable");
        }
        if (!info.ProfileWindowOpen)
        {
            return (null, null, null, $"Profile '{info.Profile}' window is not open yet");
        }
        // Only targets inside the agent's own browser context are eligible; a foreign pageId is rejected.
        var page = string.IsNullOrEmpty(pageId)
            ? DefaultTab(info.Tabs)
            : info.Tabs.FirstOrDefault(t => t.TargetId == pageId);
        return page is null ? (null, null, null, $"No tab of agent '{agentId}' matches") : (page, info.CdpUrl, info.BrowserContextId, null);
    }

    private static async Task<bool> ActivateAsync(IHttpClientFactory httpFactory, string cdpUrl, string targetId, string? targetContextId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var http = httpFactory.CreateClient(AgentBrowserDirectory.HttpClientName);

        // Under Wayland / labwc compositor, pure /json/activate only switches the active tab internally
        // but labwc denies unseeded window activation ("Denying focus request, seat wasn't supplied").
        // To guarantee the agent's profile window is brought on top of the shared desktop:
        // 1. Connect to browser WebSocket debugger.
        // 2. Find target windowIds (all windows matching the profile context or targetId).
        // 3. Minimize other profile windows so labwc un-maps them.
        // 4. Restore target profile windows (normal -> maximized) to place them on top of the window stack.
        // 5. Activate target tab. Fallback to /json/activate if browser WebSocket is unreachable.
        try
        {
            var version = await http.GetFromJsonAsync<JsonObject>($"{cdpUrl.TrimEnd('/')}/json/version", cts.Token);
            var browserWs = version?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(browserWs))
            {
                var wsUri = new UriBuilder(browserWs) { Host = new Uri(cdpUrl).Host, Port = new Uri(cdpUrl).Port }.Uri;
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(wsUri, cts.Token);

                int reqId = 100;
                await SendAsync(ws, new { id = ++reqId, method = "Target.getTargets" }, cts.Token);
                var targetsBytes = await ReceiveByIdAsync(ws, reqId, cts.Token);
                if (targetsBytes is not null)
                {
                    using var doc = JsonDocument.Parse(targetsBytes);
                    var pages = doc.RootElement.GetProperty("result").GetProperty("targetInfos").EnumerateArray()
                        .Where(t => t.TryGetProperty("type", out var type) && type.GetString() == "page")
                        .Select(t => (
                            TargetId: t.GetProperty("targetId").GetString() ?? "",
                            ContextId: t.TryGetProperty("browserContextId", out var c) ? c.GetString() : null))
                        .ToList();

                    var targetWids = new HashSet<int>();
                    var otherWids = new HashSet<int>();
                    foreach (var p in pages)
                    {
                        var callId = ++reqId;
                        await SendAsync(ws, new { id = callId, method = "Browser.getWindowForTarget", @params = new { targetId = p.TargetId } }, cts.Token);
                        var winBytes = await ReceiveByIdAsync(ws, callId, cts.Token);
                        if (winBytes is not null)
                        {
                            using var winDoc = JsonDocument.Parse(winBytes);
                            if (winDoc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("windowId", out var widProp))
                            {
                                var wid = widProp.GetInt32();
                                bool isTarget = p.TargetId == targetId
                                    || (!string.IsNullOrEmpty(targetContextId) && p.ContextId == targetContextId)
                                    || (string.IsNullOrEmpty(targetContextId) && string.IsNullOrEmpty(p.ContextId));
                                if (isTarget)
                                {
                                    targetWids.Add(wid);
                                }
                                else
                                {
                                    otherWids.Add(wid);
                                }
                            }
                        }
                    }

                    if (targetWids.Count > 0)
                    {
                        otherWids.ExceptWith(targetWids);
                        foreach (var wid in otherWids)
                        {
                            var hideId = ++reqId;
                            await SendAsync(ws, new { id = hideId, method = "Browser.setWindowBounds", @params = new { windowId = wid, bounds = new { windowState = "minimized" } } }, cts.Token);
                            await ReceiveByIdAsync(ws, hideId, cts.Token);
                        }

                        // Auto-dismiss Lens overlay or hanging lens UI targets if present
                        var lensTargets = pages.Where(p => p.TargetId != targetId && (p.TargetId.Contains("lens", StringComparison.OrdinalIgnoreCase))).ToList();
                        foreach (var lt in lensTargets)
                        {
                            var closeId = ++reqId;
                            await SendAsync(ws, new { id = closeId, method = "Target.closeTarget", @params = new { targetId = lt.TargetId } }, cts.Token);
                            await ReceiveByIdAsync(ws, closeId, cts.Token);
                        }

                        foreach (var wid in targetWids)
                        {
                            var normId = ++reqId;
                            await SendAsync(ws, new { id = normId, method = "Browser.setWindowBounds", @params = new { windowId = wid, bounds = new { left = 0, top = 0, width = 1440, height = 900, windowState = "normal" } } }, cts.Token);
                            await ReceiveByIdAsync(ws, normId, cts.Token);

                            var maxId = ++reqId;
                            await SendAsync(ws, new { id = maxId, method = "Browser.setWindowBounds", @params = new { windowId = wid, bounds = new { windowState = "maximized" } } }, cts.Token);
                            await ReceiveByIdAsync(ws, maxId, cts.Token);
                        }
                    }
                }

                var actId = ++reqId;
                await SendAsync(ws, new { id = actId, method = "Target.activateTarget", @params = new { targetId } }, cts.Token);
                await ReceiveByIdAsync(ws, actId, cts.Token);
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                return true;
            }
        }
        catch
        {
            // Fallback to HTTP /json/activate
        }

        using var res = await http.GetAsync($"{cdpUrl.TrimEnd('/')}/json/activate/{Uri.EscapeDataString(targetId)}", cts.Token);
        return res.IsSuccessStatusCode;
    }

    private static Uri PageSocket(string cdpUrl, string targetId)
    {
        var cdp = new Uri(cdpUrl);
        return new Uri($"ws://{cdp.Authority}/devtools/page/{Uri.EscapeDataString(targetId)}");
    }

    private static async Task<string?> CaptureScreenshotAsync(string cdpUrl, string targetId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(PageSocket(cdpUrl, targetId), cts.Token);
        // A tab behind another profile's window on the shared desktop is "hidden": Chromium throttles it and a
        // capture can wait seconds for a frame. Emulating focus/active lifecycle for this session makes the
        // renderer produce a frame now; both are per-session and end when this socket closes.
        await SendAsync(ws, new { id = 10, method = "Emulation.setFocusEmulationEnabled", @params = new { enabled = true } }, cts.Token);
        await SendAsync(ws, new { id = 11, method = "Page.setWebLifecycleState", @params = new { state = "active" } }, cts.Token);
        // fromSurface + optimizeForSpeed: take the composited surface instead of waiting for a fresh frame.
        await SendAsync(ws, new { id = 1, method = "Page.captureScreenshot", @params = new { format = "jpeg", quality = 60, fromSurface = true, optimizeForSpeed = true } }, cts.Token);
        while (ws.State == WebSocketState.Open)
        {
            var bytes = await ReceiveAsync(ws, cts.Token);
            if (bytes is null)
            {
                break;
            }
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1)
            {
                return doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("data", out var d) ? d.GetString() : null;
            }
        }
        return null;
    }

    private static async Task<byte[]?> ReceiveByIdAsync(WebSocket ws, int expectedId, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open)
        {
            var bytes = await ReceiveAsync(ws, ct);
            if (bytes is null) return null;
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
            {
                return bytes;
            }
        }
        return null;
    }

    private const int MaxCdpMessageBytes = 16 * 1024 * 1024;

    private static Task SendAsync(WebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);

    private static async Task<byte[]?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > MaxCdpMessageBytes)
            {
                throw new WebSocketException("CDP message exceeds limit");
            }
        } while (!r.EndOfMessage);
        return ms.ToArray();
    }
}
