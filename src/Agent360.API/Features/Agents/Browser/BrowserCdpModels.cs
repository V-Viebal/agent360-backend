namespace Agent360.API.Features.Agents.Browser;

public sealed record BrowserPageSummary(
    string Id,
    string Title,
    string Url,
    string Type,
    string? WebSocketDebuggerUrl);

public sealed record BrowserScreenshotResponse(
    string AgentId,
    string PageId,
    string Format,
    string Base64Data,
    DateTimeOffset CapturedAt);
