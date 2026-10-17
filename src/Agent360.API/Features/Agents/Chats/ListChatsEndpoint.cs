using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record QwenPawChatDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("pinned")] bool Pinned,
    [property: JsonPropertyName("archived")] bool Archived);

public sealed record AgentSessionResponse(
    string Id,
    string Title,
    string? UpdatedAt,
    int MessageCount,
    string? LastMessageSnippet,
    bool Unread);

public sealed class ListChatsEndpoint : IEndpoint
{
    public const string RouteName = "ListChats";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "chats",
            async Task<IResult> (
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var requestUri = new Uri(baseUri, "api/chats");

                var chats = await httpClient.GetFromJsonAsync<IReadOnlyList<QwenPawChatDto>>(
                    requestUri,
                    cancellationToken);

                if (chats is null)
                {
                    return TypedResults.Ok(Array.Empty<AgentSessionResponse>());
                }

                var result = chats.Select(c => new AgentSessionResponse(
                    Id: !string.IsNullOrWhiteSpace(c.Id) ? c.Id : (c.SessionId ?? ""),
                    Title: string.IsNullOrWhiteSpace(c.Name) ? "Session" : c.Name,
                    UpdatedAt: c.UpdatedAt,
                    MessageCount: 1,
                    LastMessageSnippet: c.Name,
                    Unread: false
                )).ToList();

                return TypedResults.Ok(result);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all agent chats/sessions")
            .WithDescription("Retrieves the sessions and recent chat threads mapped from QwenPaw /api/chats.")
            .Produces<IReadOnlyList<AgentSessionResponse>>()
            .AllowAnonymous();
    }
}
