using System.Collections.Concurrent;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Share;

public sealed record CreateChatShareRequest(
    string SessionId,
    string Title,
    IReadOnlyList<SharedMessageSnapshot> Messages);

public sealed record SharedMessageSnapshot(
    string Id,
    string Role,
    string Content,
    string? Timestamp);

public sealed record ChatShareResponse(
    string ShareToken,
    string ShareUrl,
    string Title,
    int MessageCount,
    DateTimeOffset CreatedAt);

public sealed class ShareChatEndpoint : IEndpoint
{
    private static readonly ConcurrentDictionary<string, CreateChatShareRequest> ShareStore = new();

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. Create a shareable read-only snapshot of a chat
        routeBuilder.MapPost(
            "chats/{sessionId}/share",
            (
                string sessionId,
                [FromBody] CreateChatShareRequest request,
                HttpContext httpContext) =>
            {
                var token = $"share_{Guid.NewGuid():N}";
                ShareStore[token] = request;

                var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
                var shareUrl = $"{baseUrl}/api/v1/shared/{token}";

                return TypedResults.Ok(new ChatShareResponse(
                    ShareToken: token,
                    ShareUrl: shareUrl,
                    Title: request.Title,
                    MessageCount: request.Messages?.Count ?? 0,
                    CreatedAt: DateTimeOffset.UtcNow));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("CreateChatShareSnapshot")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Generate a public read-only snapshot link for a chat session")
            .AllowAnonymous();

        // 2. View shared chat snapshot
        routeBuilder.MapGet(
            "shared/{shareToken}",
            (string shareToken) =>
            {
                if (ShareStore.TryGetValue(shareToken, out var snapshot))
                {
                    return Results.Ok(snapshot);
                }

                return Results.NotFound(new { message = "Shared chat snapshot expired or not found." });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetSharedChatSnapshot")
            .WithTags(EndpointTags.Agents)
            .WithSummary("View shared read-only chat snapshot")
            .AllowAnonymous();
    }
}
