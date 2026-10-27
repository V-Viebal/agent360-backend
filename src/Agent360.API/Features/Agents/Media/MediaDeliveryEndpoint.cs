using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Media;

public sealed class MediaDeliveryEndpoint : IEndpoint
{
    private static readonly string MediaStoragePath = Path.Combine(AppContext.BaseDirectory, "_storage", "media");

    static MediaDeliveryEndpoint()
    {
        if (!Directory.Exists(MediaStoragePath))
        {
            Directory.CreateDirectory(MediaStoragePath);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. Upload media / attachments (Image, PDF, Audio, Video)
        routeBuilder.MapPost(
            "media/upload",
            async Task<IResult> (
                IFormFile file,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                if (file is null || file.Length == 0)
                {
                    return TypedResults.BadRequest(new { message = "No valid file uploaded." });
                }

                var fileId = Guid.NewGuid().ToString("N");
                var ext = Path.GetExtension(file.FileName);
                var safeStoredName = $"{fileId}{ext}";
                var fullPath = Path.Combine(MediaStoragePath, safeStoredName);

                await using (var stream = new FileStream(fullPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, cancellationToken);
                }

                var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
                var publicUrl = $"{baseUrl}/api/v1/media/files/{safeStoredName}";

                return TypedResults.Ok(new MediaUploadResponse(
                    FileId: safeStoredName,
                    FileName: file.FileName,
                    ContentType: file.ContentType,
                    Size: file.Length,
                    PublicUrl: publicUrl,
                    UploadedAt: DateTimeOffset.UtcNow));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("UploadMedia")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Upload image, document, or audio attachment for agent session")
            .DisableAntiforgery()
            .AllowAnonymous();

        // 2. Serve static media files securely
        routeBuilder.MapGet(
            "media/files/{fileName}",
            (string fileName) =>
            {
                var safeName = Path.GetFileName(fileName);
                var filePath = Path.Combine(MediaStoragePath, safeName);

                if (!File.Exists(filePath))
                {
                    return Results.NotFound(new { message = "Media file not found." });
                }

                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var contentType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".pdf" => "application/pdf",
                    ".mp3" => "audio/mpeg",
                    ".mp4" => "video/mp4",
                    _ => "application/octet-stream"
                };

                return Results.File(filePath, contentType);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetMediaFile")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Retrieve or stream uploaded media file")
            .AllowAnonymous();

        // 3. Dispatch media message into agent's chat session
        routeBuilder.MapPost(
            "agents/{agentId}/media/send",
            async (
                string agentId,
                [FromBody] SendMediaMessageRequest request,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
                var messageId = $"msg-{Guid.NewGuid():N}";
                var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
                var mediaUrl = $"{baseUrl}/api/v1/media/files/{request.FileId}";

                var response = new SendMediaMessageResponse(
                    MessageId: messageId,
                    SessionId: request.SessionId,
                    Status: "dispatched",
                    MediaUrl: mediaUrl,
                    Caption: request.Caption,
                    Timestamp: DateTimeOffset.UtcNow);

                return TypedResults.Ok(response);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SendMediaMessageToAgent")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Dispatch an image/media attachment into agent session queue")
            .AllowAnonymous();
    }
}
