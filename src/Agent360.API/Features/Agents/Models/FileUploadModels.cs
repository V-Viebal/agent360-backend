using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record FileUploadResponse
{
    public FileUploadResponse() { }

    public FileUploadResponse(
        string FileId,
        string Filename,
        string VirtualUri,
        long SizeBytes,
        string ContentType,
        DateTime UploadedAt,
        string? Url = null,
        string? FileName = null,
        long? Size = null,
        string? MimeType = null,
        string? PreviewUrl = null)
    {
        this.FileId = FileId;
        this.Filename = Filename;
        this.VirtualUri = VirtualUri;
        this.SizeBytes = SizeBytes;
        this.ContentType = ContentType;
        this.UploadedAt = UploadedAt;
        this.Url = Url;
        this.FileName = FileName;
        this.Size = Size;
        this.MimeType = MimeType;
        this.PreviewUrl = PreviewUrl;
    }

    [JsonPropertyName("file_id")]
    public string FileId { get; init; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; init; } = "";

    [JsonPropertyName("virtual_uri")]
    public string VirtualUri { get; init; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; init; } = "";

    [JsonPropertyName("uploaded_at")]
    public DateTime UploadedAt { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("preview_url")]
    public string? PreviewUrl { get; init; }
}
