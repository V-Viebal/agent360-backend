using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record FileUploadResponse(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("virtual_uri")] string VirtualUri,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("uploaded_at")] DateTime UploadedAt);
