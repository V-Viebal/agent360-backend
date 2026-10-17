using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record BrowserCdpVersionDto(
    [property: JsonPropertyName("Browser")] string? Browser,
    [property: JsonPropertyName("Protocol-Version")] string? ProtocolVersion,
    [property: JsonPropertyName("User-Agent")] string? UserAgent,
    [property: JsonPropertyName("V8-Version")] string? V8Version,
    [property: JsonPropertyName("WebKit-Version")] string? WebKitVersion,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

public sealed record BrowserCdpTargetDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("devtoolsFrontendUrl")] string? DevtoolsFrontendUrl,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl,
    [property: JsonPropertyName("faviconUrl")] string? FaviconUrl);

public sealed record BrowserCdpStatusResponse(
    bool Available,
    string Endpoint,
    string? BrowserVersion,
    int TargetCount,
    IReadOnlyList<BrowserCdpTargetDto> Targets);
