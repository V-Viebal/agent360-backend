using Agent360.API.Features.Agents.Services;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Browser;

public sealed class AgentBrowserCdpEndpoint : IEndpoint
{
    private const string DefaultCdpHttpHost = "http://100.76.185.111:19223";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List active browser pages for an agent (routed to node CDP endpoint)
        routeBuilder.MapGet(
            "agents/{agentId}/browser/pages",
            async Task<IResult> (
                string agentId,
                IHttpClientFactory httpClientFactory,
                IFleetInstanceRoutingService routingService,
                CancellationToken cancellationToken) =>
            {
                var target = routingService.ResolveTarget(agentId);
                using var httpClient = httpClientFactory.CreateClient();
                var pages = await GetBrowserPagesAsync(httpClient, target.CdpUrl, cancellationToken);
                return TypedResults.Ok(pages);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserPages")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List active browser pages for agent")
            .AllowAnonymous();

        // 2. Snapshot screenshot for quick inspection
        routeBuilder.MapGet(
            "agents/{agentId}/browser/screenshot",
            async Task<IResult> (
                string agentId,
                IHttpClientFactory httpClientFactory,
                IFleetInstanceRoutingService routingService,
                [FromQuery] string? pageId,
                CancellationToken cancellationToken) =>
            {
                var target = routingService.ResolveTarget(agentId);
                using var httpClient = httpClientFactory.CreateClient();
                var pages = await GetBrowserPagesAsync(httpClient, target.CdpUrl, cancellationToken);
                var targetPage = string.IsNullOrEmpty(pageId)
                    ? pages.FirstOrDefault(p => p.Type == "page")
                    : pages.FirstOrDefault(p => p.Id == pageId);

                if (targetPage is null)
                {
                    return TypedResults.NotFound(new { message = $"No open browser page found for agent {agentId}" });
                }

                var screenshotBase64 = await CaptureScreenshotAsync(targetPage.Id, target.CdpUrl, cancellationToken);
                if (screenshotBase64 is null)
                {
                    return TypedResults.Problem("Failed to capture screenshot from CDP target.");
                }

                return TypedResults.Ok(new BrowserScreenshotResponse(
                    AgentId: agentId,
                    PageId: targetPage.Id,
                    Format: "jpeg",
                    Base64Data: screenshotBase64,
                    CapturedAt: DateTimeOffset.UtcNow));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserScreenshot")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Capture live screenshot of agent browser")
            .AllowAnonymous();

        // 3. Raw JPEG image endpoint
        routeBuilder.MapGet(
            "agents/{agentId}/browser/image.jpg",
            async Task<IResult> (
                string agentId,
                IHttpClientFactory httpClientFactory,
                IFleetInstanceRoutingService routingService,
                [FromQuery] string? pageId,
                CancellationToken cancellationToken) =>
            {
                var target = routingService.ResolveTarget(agentId);
                using var httpClient = httpClientFactory.CreateClient();
                var pages = await GetBrowserPagesAsync(httpClient, target.CdpUrl, cancellationToken);
                var targetPage = string.IsNullOrEmpty(pageId)
                    ? pages.FirstOrDefault(p => p.Type == "page")
                    : pages.FirstOrDefault(p => p.Id == pageId);

                if (targetPage is null)
                {
                    return TypedResults.NotFound(new { message = "No browser page available" });
                }

                var screenshotBase64 = await CaptureScreenshotAsync(targetPage.Id, target.CdpUrl, cancellationToken);
                if (screenshotBase64 is null)
                {
                    return TypedResults.Problem("Failed to capture screenshot");
                }

                var imageBytes = Convert.FromBase64String(screenshotBase64);
                return TypedResults.File(imageBytes, "image/jpeg");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetAgentBrowserRawImage")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get direct image JPEG of agent browser viewport")
            .AllowAnonymous();

        // 4. Real-time WebSocket Screencast Bridge
        routeBuilder.Map(
            "/api/v1/agents/{agentId}/browser/stream",
            async (string agentId, HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("WebSocket connection expected.");
                    return;
                }

                using var clientWs = await context.WebSockets.AcceptWebSocketAsync();

                // Get target page
                var routingService = context.RequestServices.GetRequiredService<IFleetInstanceRoutingService>();
                var target = routingService.ResolveTarget(agentId);
                using var http = new HttpClient();
                var pages = await GetBrowserPagesAsync(http, target.CdpUrl, context.RequestAborted);
                var page = pages.FirstOrDefault(p => p.Type == "page");
                if (page is null)
                {
                    await clientWs.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "No browser page available", CancellationToken.None);
                    return;
                }

                using var cdpWs = new ClientWebSocket();
                var wsHost = new Uri(target.CdpUrl).Authority;
                var cdpUri = new Uri($"ws://{wsHost}/devtools/page/{page.Id}");
                await cdpWs.ConnectAsync(cdpUri, context.RequestAborted);

                // Start Screencast on CDP
                var startMsg = JsonSerializer.Serialize(new
                {
                    id = 1001,
                    method = "Page.startScreencast",
                    @params = new { format = "jpeg", quality = 70, everyNthFrame = 1 }
                });
                var startBytes = Encoding.UTF8.GetBytes(startMsg);
                await cdpWs.SendAsync(new ArraySegment<byte>(startBytes), WebSocketMessageType.Text, true, context.RequestAborted);

                // Bi-directional pump
                var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var cdpToClient = PumpCdpToClientAsync(cdpWs, clientWs, cts.Token);
                var clientToCdp = PumpClientToCdpAsync(clientWs, cdpWs, cts.Token);

                await Task.WhenAny(cdpToClient, clientToCdp);
                cts.Cancel();
            });
    }

    private static async Task<IReadOnlyList<BrowserPageSummary>> GetBrowserPagesAsync(HttpClient httpClient, string? cdpHost, CancellationToken cancellationToken)
    {
        try
        {
            var host = !string.IsNullOrWhiteSpace(cdpHost) ? cdpHost : DefaultCdpHttpHost;
            var url = $"{host.TrimEnd('/')}/json/list";
            var nodes = await httpClient.GetFromJsonAsync<JsonArray>(url, cancellationToken);
            if (nodes is null) return Array.Empty<BrowserPageSummary>();

            var list = new List<BrowserPageSummary>();
            foreach (var node in nodes)
            {
                if (node is JsonObject obj)
                {
                    list.Add(new BrowserPageSummary(
                        Id: obj["id"]?.ToString() ?? "",
                        Title: obj["title"]?.ToString() ?? "",
                        Url: obj["url"]?.ToString() ?? "",
                        Type: obj["type"]?.ToString() ?? "page",
                        WebSocketDebuggerUrl: obj["webSocketDebuggerUrl"]?.ToString()));
                }
            }
            return list;
        }
        catch
        {
            return Array.Empty<BrowserPageSummary>();
        }
    }

    private static async Task<string?> CaptureScreenshotAsync(string pageId, string? cdpHost, CancellationToken cancellationToken)
    {
        using var ws = new ClientWebSocket();
        var host = !string.IsNullOrWhiteSpace(cdpHost) ? new Uri(cdpHost).Authority : "100.76.185.111:19223";
        var uri = new Uri($"ws://{host}/devtools/page/{pageId}");
        await ws.ConnectAsync(uri, cancellationToken);

        var req = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Page.captureScreenshot",
            @params = new { format = "jpeg", quality = 65 }
        });
        var bytes = Encoding.UTF8.GetBytes(req);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);

        var buffer = new byte[1024 * 64];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString(ms.ToArray());
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                    resultEl.TryGetProperty("data", out var dataEl))
                {
                    return dataEl.GetString();
                }
                break;
            }
        }
        return null;
    }

    private static async Task PumpCdpToClientAsync(ClientWebSocket cdpWs, WebSocket clientWs, CancellationToken ct)
    {
        var buffer = new byte[1024 * 128];
        while (!ct.IsCancellationRequested && cdpWs.State == WebSocketState.Open && clientWs.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await cdpWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);

            var rawBytes = ms.ToArray();
            var text = Encoding.UTF8.GetString(rawBytes);

            // If it's a screencast frame, ACK it automatically back to CDP to sustain smooth stream
            if (text.Contains("Page.screencastFrame"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("params", out var p) &&
                        p.TryGetProperty("sessionId", out var sId))
                    {
                        var ack = JsonSerializer.Serialize(new
                        {
                            id = 2002,
                            method = "Page.screencastFrameAck",
                            @params = new { sessionId = sId.GetInt32() }
                        });
                        var ackBytes = Encoding.UTF8.GetBytes(ack);
                        await cdpWs.SendAsync(new ArraySegment<byte>(ackBytes), WebSocketMessageType.Text, true, ct);
                    }
                }
                catch { }
            }

            // Forward text frame to client
            await clientWs.SendAsync(new ArraySegment<byte>(rawBytes), WebSocketMessageType.Text, true, ct);
        }
    }

    private static async Task PumpClientToCdpAsync(WebSocket clientWs, ClientWebSocket cdpWs, CancellationToken ct)
    {
        var buffer = new byte[1024 * 32];
        while (!ct.IsCancellationRequested && clientWs.State == WebSocketState.Open && cdpWs.State == WebSocketState.Open)
        {
            var res = await clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (res.MessageType == WebSocketMessageType.Close) break;

            // Forward input / commands from client to CDP (e.g. Mouse/Key events)
            await cdpWs.SendAsync(new ArraySegment<byte>(buffer, 0, res.Count), res.MessageType, res.EndOfMessage, ct);
        }
    }
}
