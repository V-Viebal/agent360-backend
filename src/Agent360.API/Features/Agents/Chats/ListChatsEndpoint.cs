using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;
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
    [property: JsonPropertyName("last_finished_at")] string? LastFinishedAt,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("pinned")] bool Pinned,
    [property: JsonPropertyName("archived")] bool Archived);

public sealed record AgentSessionResponse(
    string Id,
    string Title,
    string? UpdatedAt,
    int MessageCount,
    string? LastMessageSnippet,
    bool Unread,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("channel")] string? Channel = null,
    [property: JsonPropertyName("status")] string? Status = "idle",
    [property: JsonPropertyName("is_running")] bool IsRunning = false);

public sealed class ListChatsEndpoint : IEndpoint
{
    public const string RouteName = "ListChats";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "chats",
            async Task<IResult> (
                [FromQuery] string? agentId,
                [FromQuery] string? channel,
                IFleetInstanceRoutingService routing,
                SessionQueueManager queueManager,
                CancellationToken cancellationToken) =>
            {
                IReadOnlyList<QwenPawChatDto>? chats;
                try
                {
                    var target = routing.ResolveTarget(agentId);
                    using var response = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/chats", ct: cancellationToken);
                    response.EnsureSuccessStatusCode();
                    chats = await response.Content.ReadFromJsonAsync<IReadOnlyList<QwenPawChatDto>>(cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    chats = Array.Empty<QwenPawChatDto>();
                }

                if (chats is null)
                {
                    return TypedResults.Ok(Array.Empty<AgentSessionResponse>());
                }

                var query = chats.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(channel))
                {
                    query = query.Where(c => string.Equals(c.Channel, channel, StringComparison.OrdinalIgnoreCase));
                }

                var result = query.Select(c =>
                {
                    var id = !string.IsNullOrWhiteSpace(c.Id) ? c.Id : (c.SessionId ?? "");
                    bool rawRunning = string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Status, "in_progress", StringComparison.OrdinalIgnoreCase);

                    if (rawRunning && !string.IsNullOrWhiteSpace(c.LastFinishedAt) && !string.IsNullOrWhiteSpace(c.UpdatedAt))
                    {
                        if (DateTime.TryParse(c.LastFinishedAt, out var finished) && DateTime.TryParse(c.UpdatedAt, out var updated))
                        {
                            if (finished >= updated.AddSeconds(-2))
                            {
                                rawRunning = false;
                            }
                        }
                    }

                    var isRunning = queueManager.IsSessionTurnActive(id)
                        || (!string.IsNullOrWhiteSpace(c.SessionId) && queueManager.IsSessionTurnActive(c.SessionId))
                        || rawRunning;

                    var isUnread = ChannelResponseStore.IsUnread(id)
                        || (!string.IsNullOrWhiteSpace(c.SessionId) && ChannelResponseStore.IsUnread(c.SessionId));

                    return new AgentSessionResponse(
                        Id: id,
                        Title: string.IsNullOrWhiteSpace(c.Name) ? "Session" : c.Name,
                        UpdatedAt: c.UpdatedAt,
                        MessageCount: 1,
                        LastMessageSnippet: c.Name,
                        Unread: isUnread,
                        SessionId: c.SessionId,
                        Channel: c.Channel,
                        Status: isRunning ? "running" : (c.Status ?? "idle"),
                        IsRunning: isRunning
                    );
                }).ToList();

                return TypedResults.Ok(result);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all agent chats/sessions")
            .WithDescription("Retrieves the sessions and recent chat threads mapped from QwenPaw /api/chats.")
            .Produces<IReadOnlyList<AgentSessionResponse>>()
            .AllowAnonymous();

        // Mark session as read
        routeBuilder.MapPost(
            "chats/{chatId}/read",
            async (string chatId, [FromQuery] string? agentId, IFleetInstanceRoutingService routing, CancellationToken ct) =>
            {
                ChannelResponseStore.MarkRead(chatId);
                try
                {
                    var target = routing.ResolveTarget(agentId);
                    using var response = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/chats", ct: ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var chats = await response.Content.ReadFromJsonAsync<IReadOnlyList<QwenPawChatDto>>(ct);
                        var matched = chats?.FirstOrDefault(c => string.Equals(c.Id, chatId, StringComparison.OrdinalIgnoreCase) || string.Equals(c.SessionId, chatId, StringComparison.OrdinalIgnoreCase));
                        if (matched is not null)
                        {
                            if (!string.IsNullOrWhiteSpace(matched.Id)) ChannelResponseStore.MarkRead(matched.Id);
                            if (!string.IsNullOrWhiteSpace(matched.SessionId)) ChannelResponseStore.MarkRead(matched.SessionId);
                        }
                    }
                }
                catch {}
                return TypedResults.Ok(new { success = true, chat_id = chatId, unread = false });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("MarkChatAsRead")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Mark chat session as read")
            .AllowAnonymous();

        routeBuilder.MapPost(
            "agents/{agentId}/chats/{chatId}/read",
            async (string agentId, string chatId, IFleetInstanceRoutingService routing, CancellationToken ct) =>
            {
                ChannelResponseStore.MarkRead(chatId);
                try
                {
                    var target = routing.ResolveTarget(agentId);
                    using var response = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/chats", ct: ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var chats = await response.Content.ReadFromJsonAsync<IReadOnlyList<QwenPawChatDto>>(ct);
                        var matched = chats?.FirstOrDefault(c => string.Equals(c.Id, chatId, StringComparison.OrdinalIgnoreCase) || string.Equals(c.SessionId, chatId, StringComparison.OrdinalIgnoreCase));
                        if (matched is not null)
                        {
                            if (!string.IsNullOrWhiteSpace(matched.Id)) ChannelResponseStore.MarkRead(matched.Id);
                            if (!string.IsNullOrWhiteSpace(matched.SessionId)) ChannelResponseStore.MarkRead(matched.SessionId);
                        }
                    }
                }
                catch {}
                return TypedResults.Ok(new { success = true, agent_id = agentId, chat_id = chatId, unread = false });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("MarkAgentChatAsRead")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Mark specific agent chat session as read")
            .AllowAnonymous();
    }
}
