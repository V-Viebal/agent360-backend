using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Options;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Services;

public sealed class BrowserCdpService : IBrowserCdpService
{
    private readonly HttpClient _httpClient;
    private readonly IFleetInstanceRoutingService _routingService;
    private readonly ILogger<BrowserCdpService> _logger;
    private readonly QwenPawOptions _options;

    public BrowserCdpService(
        HttpClient httpClient,
        IFleetInstanceRoutingService routingService,
        IOptions<QwenPawOptions> options,
        ILogger<BrowserCdpService> logger)
    {
        _httpClient = httpClient;
        _routingService = routingService;
        _logger = logger;
        _options = options.Value;

        var baseUri = !string.IsNullOrWhiteSpace(_options.BrowserCdpUrl)
            ? _options.BrowserCdpUrl.TrimEnd('/') + "/"
            : "http://100.76.185.111:19223/";

        _httpClient.BaseAddress = new Uri(baseUri);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Min(15, _options.TimeoutSeconds));
    }

    public async Task<BrowserCdpStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await _httpClient.GetFromJsonAsync<BrowserCdpVersionDto>("json/version", cancellationToken);
            var targets = await GetTargetsAsync(cancellationToken);

            return new BrowserCdpStatusResponse(
                Available: true,
                Endpoint: _httpClient.BaseAddress?.ToString() ?? "unknown",
                BrowserVersion: version?.Browser ?? "Chromium",
                TargetCount: targets.Count,
                Targets: targets);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CDP status probe failed at {Endpoint}", _httpClient.BaseAddress);
            return new BrowserCdpStatusResponse(
                Available: false,
                Endpoint: _httpClient.BaseAddress?.ToString() ?? "unknown",
                BrowserVersion: null,
                TargetCount: 0,
                Targets: Array.Empty<BrowserCdpTargetDto>());
        }
    }

    public async Task<IReadOnlyList<BrowserCdpTargetDto>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var targets = await _httpClient.GetFromJsonAsync<IReadOnlyList<BrowserCdpTargetDto>>("json/list", cancellationToken);
            return targets ?? Array.Empty<BrowserCdpTargetDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve CDP target list");
            return Array.Empty<BrowserCdpTargetDto>();
        }
    }

    public async Task<byte[]> CaptureScreenshotAsync(string? targetId = null, CancellationToken cancellationToken = default)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        BrowserCdpTargetDto? pageTarget = null;

        if (!string.IsNullOrWhiteSpace(targetId))
        {
            pageTarget = targets.FirstOrDefault(t => t.Id == targetId);
        }

        pageTarget ??= targets.FirstOrDefault(t => t.Type == "page" && !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl))
                     ?? targets.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.WebSocketDebuggerUrl));

        if (pageTarget?.WebSocketDebuggerUrl == null)
        {
            throw new InvalidOperationException("No suitable active browser page target found for CDP capture.");
        }

        // Connect over WebSocket to capture screenshot
        var wsUriString = pageTarget.WebSocketDebuggerUrl;

        // If wsDebugger points to localhost or private container ip, rewrite with authoritative baseUri
        var baseUri = _httpClient.BaseAddress!;
        if (wsUriString.Contains("ws://localhost") || wsUriString.Contains("ws://127.0.0.1"))
        {
            var path = new Uri(wsUriString).PathAndQuery;
            var hostPort = baseUri.Authority;
            wsUriString = $"ws://{hostPort}{path}";
        }

        using var ws = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await ws.ConnectAsync(new Uri(wsUriString), cts.Token);

        // Command: Page.captureScreenshot
        var cmdId = 101;
        var cmd = JsonSerializer.Serialize(new
        {
            id = cmdId,
            method = "Page.captureScreenshot",
            @params = new { format = "jpeg", quality = 80 }
        });

        var bytes = Encoding.UTF8.GetBytes(cmd);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

        var buffer = new byte[64 * 1024];
        var responseBuilder = new StringBuilder();

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            responseBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                break;
            }
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        var json = JsonDocument.Parse(responseBuilder.ToString());
        if (json.RootElement.TryGetProperty("result", out var resultProp) &&
            resultProp.TryGetProperty("data", out var dataProp))
        {
            var base64 = dataProp.GetString();
            if (!string.IsNullOrWhiteSpace(base64))
            {
                return Convert.FromBase64String(base64);
            }
        }

        throw new InvalidOperationException("Page.captureScreenshot returned no valid image data.");
    }
}
