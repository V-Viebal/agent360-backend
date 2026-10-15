using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Tools;

public sealed record QwenPawBuiltinToolDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("async_execution")] bool AsyncExecution,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("requires_config")] bool RequiresConfig);

public sealed record BuiltinToolSummaryResponse(
    string Name,
    string Description,
    bool Enabled,
    bool AsyncExecution,
    string? Icon,
    bool RequiresConfig);

public sealed record ToggleToolRequest(
    bool Enabled);
