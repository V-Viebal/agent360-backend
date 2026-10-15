using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Mcps;

public sealed record QwenPawMcpDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("transport")] string? Transport,
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("url")] string? Url);

public sealed record McpServerSummaryResponse(
    string Key,
    string Name,
    string Description,
    bool Enabled,
    string Transport,
    string? Command,
    string? Url);

public sealed record ToggleMcpRequest(
    bool Enabled);
