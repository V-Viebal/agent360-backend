using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record QwenPawWorkspaceTreeResponse(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("entries")] IReadOnlyList<QwenPawWorkspaceEntryDto>? Entries,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record QwenPawWorkspaceEntryDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("preview_kind")] string? PreviewKind,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("modified_at")] string? ModifiedAt);
