namespace Agent360.API.Features.Agents.Media;

public sealed record MediaUploadResponse(
    string FileId,
    string FileName,
    string ContentType,
    long Size,
    string PublicUrl,
    DateTimeOffset UploadedAt);

public sealed record SendMediaMessageRequest(
    string SessionId,
    string FileId,
    string? Caption,
    string? Role = "user");

public sealed record SendMediaMessageResponse(
    string MessageId,
    string SessionId,
    string Status,
    string? MediaUrl,
    string? Caption,
    DateTimeOffset Timestamp);
