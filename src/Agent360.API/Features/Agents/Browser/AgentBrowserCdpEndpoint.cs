using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>
/// Agent-scoped browser surface. Every call is confined to the agent's own Chromium profile
/// (browser context): pages, screenshots and the screencast never show another agent's tabs.
/// </summary>
public sealed class AgentBrowserCdpEndpoint : IEndpoint
{
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
            .AllowAnonymous();

        // Bring the agent's own profile window to the front of the shared Selkies desktop
        // before the viewer opens it, so the user sees this agent's tabs, not the last agent's.
        routeBuilder.MapPost(
            "agents/{agentId}/browser/focus",
            async Task<IResult> (string agentId, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                var (page, cdp, error) = await PickPageAsync(directory, agentId, null, ct);
                if (page is null)
                {
                    return TypedResults.Conflict(new { message = error });
                }
                var ok = await ActivateAsync(cdp!, page.TargetId, ct);
                return ok
                    ? TypedResults.Ok(new { agentId, targetId = page.TargetId, url = page.Url })
                    : TypedResults.Problem("Failed to focus the agent's browser window", statusCode: StatusCodes.Status502BadGateway);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("FocusAgentBrowser")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Bring the agent's own Chromium profile window to the front of the desktop")
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
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/screenshot",
            async Task<IResult> (string agentId, [FromQuery] string? pageId, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                var (page, cdp, error) = await PickPageAsync(directory, agentId, pageId, ct);
                if (page is null)
                {
                    return TypedResults.NotFound(new { message = error });
                }
                var data = await CaptureScreenshotAsync(cdp!, page.TargetId, ct);
                return data is null
                    ? TypedResults.Problem("Failed to capture screenshot from CDP target.", statusCode: StatusCodes.Status502BadGateway)
                    : TypedResults.Ok(new BrowserScreenshotResponse(agentId, page.TargetId, "jpeg", data, DateTimeOffset.UtcNow));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserScreenshot")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Capture a screenshot of the agent's own browser tab")
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/{agentId}/browser/image.jpg",
            async Task<IResult> (string agentId, [FromQuery] string? pageId, AgentBrowserDirectory directory, CancellationToken ct) =>
            {
                var (page, cdp, error) = await PickPageAsync(directory, agentId, pageId, ct);
                if (page is null)
                {
                    return TypedResults.NotFound(new { message = error });
                }
                var data = await CaptureScreenshotAsync(cdp!, page.TargetId, ct);
                return data is null
                    ? TypedResults.Problem("Failed to capture screenshot", statusCode: StatusCodes.Status502BadGateway)
                    : TypedResults.File(Convert.FromBase64String(data), "image/jpeg");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserRawImage")
            .WithTags(EndpointTags.Agents)
            .WithSummary("JPEG of the agent's own browser tab")
            .AllowAnonymous();

        // Live screencast of the agent's tab (inspection only; interactive control is the Selkies viewer).
        routeBuilder.Map(
            "/api/v1/agents/{agentId}/browser/stream",
            async (string agentId, HttpContext context, AgentBrowserDirectory directory) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("WebSocket connection expected.");
                    return;
                }
                var ct = context.RequestAborted;
                var (page, cdp, error) = await PickPageAsync(directory, agentId, context.Request.Query["pageId"], ct);
                using var clientWs = await context.WebSockets.AcceptWebSocketAsync();
                if (page is null)
                {
                    await clientWs.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, Truncate(error, 120), CancellationToken.None);
                    return;
                }
                var maxWidth = Math.Clamp(int.TryParse(context.Request.Query["maxWidth"], out var w) ? w : 1280, 320, 1920);
                var quality = Math.Clamp(int.TryParse(context.Request.Query["quality"], out var q) ? q : 55, 20, 85);
                await ScreencastAsync(cdp!, page.TargetId, clientWs, maxWidth, quality, ct);
            });
    }

    private static string Truncate(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];

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

    private static async Task<bool> ActivateAsync(string cdpUrl, string targetId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        using var http = new HttpClient();
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
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(PageSocket(cdpUrl, targetId), cts.Token);
        await SendAsync(ws, new { id = 1, method = "Page.captureScreenshot", @params = new { format = "jpeg", quality = 65 } }, cts.Token);
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

    private static async Task ScreencastAsync(string cdpUrl, string targetId, WebSocket clientWs, int maxWidth, int quality, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cdpWs = new ClientWebSocket();
        cdpWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await cdpWs.ConnectAsync(PageSocket(cdpUrl, targetId), cts.Token);
        // everyNthFrame=2 + maxWidth keeps phones and slow links smooth; frames are ACKed only after the
        // client accepted the previous one, so a slow viewer gets fewer frames instead of a growing queue.
        await SendAsync(cdpWs, new { id = 1, method = "Page.startScreencast", @params = new { format = "jpeg", quality, maxWidth, maxHeight = maxWidth, everyNthFrame = 2 } }, cts.Token);

        var fromClient = Task.Run(async () =>
        {
            var buf = new byte[4096];
            while (clientWs.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var r = await clientWs.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }, cts.Token);

        try
        {
            while (cdpWs.State == WebSocketState.Open && clientWs.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var receive = ReceiveAsync(cdpWs, cts.Token);
                if (await Task.WhenAny(receive, fromClient) == fromClient)
                {
                    break;
                }
                var bytes = await receive;
                if (bytes is null)
                {
                    break;
                }
                using var doc = JsonDocument.Parse(bytes);
                if (!doc.RootElement.TryGetProperty("method", out var method) || method.GetString() != "Page.screencastFrame")
                {
                    continue;
                }
                var p = doc.RootElement.GetProperty("params");
                var frame = JsonSerializer.SerializeToUtf8Bytes(new { method = "Page.screencastFrame", @params = new { data = p.GetProperty("data").GetString() } });
                using (var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                {
                    sendCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await clientWs.SendAsync(frame, WebSocketMessageType.Text, true, sendCts.Token);
                }
                await SendAsync(cdpWs, new { id = 2, method = "Page.screencastFrameAck", @params = new { sessionId = p.GetProperty("sessionId").GetInt32() } }, cts.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            cts.Cancel();
            if (cdpWs.State == WebSocketState.Open)
            {
                try { await SendAsync(cdpWs, new { id = 3, method = "Page.stopScreencast" }, CancellationToken.None); } catch { }
            }
            if (clientWs.State == WebSocketState.Open)
            {
                try { await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            }
        }
    }

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
        } while (!r.EndOfMessage);
        return ms.ToArray();
    }
}
