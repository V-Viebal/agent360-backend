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

        // Bring the agent's own profile window to the front of the shared Selkies desktop
        // before the viewer opens it, so the user sees this agent's tabs, not the last agent's.
        routeBuilder.MapPost(
            "agents/{agentId}/browser/focus",
            async Task<IResult> (string agentId, AgentBrowserDirectory directory, IHttpClientFactory httpFactory, CancellationToken ct) =>
            {
                var (page, cdp, error) = await PickPageAsync(directory, agentId, null, ct);
                if (page is null)
                {
                    return TypedResults.Conflict(new { message = error });
                }
                var ok = await ActivateAsync(httpFactory, cdp!, page.TargetId, ct);
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
                var (page, cdp, error) = await PickPageAsync(directory, agentId, pageId, ct);
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
                var (page, cdp, error) = await PickPageAsync(directory, agentId, pageId, ct);
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
                var (page, cdp, error) = await PickPageAsync(directory, agentId, context.Request.Query["pageId"], ct);
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
        // Same-origin (UI and API behind one host via the frontend nginx /api proxy).
        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
            && string.Equals(o.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
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

    private static async Task<(AgentBrowserTab? Page, string? Cdp, string? Error)> PickPageAsync(
        AgentBrowserDirectory directory, string agentId, string? pageId, CancellationToken ct)
    {
        var info = await directory.GetAsync(agentId, ct);
        if (info is null)
        {
            return (null, null, $"Agent '{agentId}' has no mapped browser profile");
        }
        if (!info.CdpReachable)
        {
            return (null, null, $"Browser {info.Service} is not reachable");
        }
        if (!info.ProfileWindowOpen)
        {
            return (null, null, $"Profile '{info.Profile}' window is not open yet");
        }
        // Only targets inside the agent's own browser context are eligible; a foreign pageId is rejected.
        var page = string.IsNullOrEmpty(pageId)
            ? info.Tabs.FirstOrDefault(t => !t.Anchor) ?? info.Tabs.FirstOrDefault()
            : info.Tabs.FirstOrDefault(t => t.TargetId == pageId);
        return page is null ? (null, null, $"No tab of agent '{agentId}' matches") : (page, info.CdpUrl, null);
    }

    private static async Task<bool> ActivateAsync(IHttpClientFactory httpFactory, string cdpUrl, string targetId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var http = httpFactory.CreateClient(AgentBrowserDirectory.HttpClientName);
        // /json/activate focuses the tab and raises its window (no browser-level websocket needed).
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
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(PageSocket(cdpUrl, targetId), cts.Token);
        // fromSurface + optimizeForSpeed: take the composited surface instead of waiting for a fresh frame,
        // which a background (throttled) tab may never produce.
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
