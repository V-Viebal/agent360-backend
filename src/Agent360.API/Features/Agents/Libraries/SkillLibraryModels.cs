using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Libraries;

public sealed record QwenPawSkillDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("emoji")] string? Emoji,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("channels")] IReadOnlyList<string>? Channels,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("last_updated")] string? LastUpdated);

public sealed record SkillLibraryItemResponse(
    string Name,
    string Description,
    string Source,
    string? Emoji,
    bool Enabled,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> Tags,
    string? LastUpdated);

public sealed record ToggleSkillRequest(
    bool Enabled);
