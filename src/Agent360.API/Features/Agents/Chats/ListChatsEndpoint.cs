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

public sealed record UpdateChatTitleRequest(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("pinned")] bool? Pinned = null,
    [property: JsonPropertyName("archived")] bool? Archived = null);

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
    [property: JsonPropertyName("is_running")] bool IsRunning = false,
    [property: JsonPropertyName("pinned")] bool Pinned = false,
    [property: JsonPropertyName("archived")] bool Archived = false);

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
                HttpContext httpContext,
                IQwenPawClient client,
                SessionQueueManager queueManager,
                CancellationToken cancellationToken) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, cancellationToken) is { } denied) return denied;
                httpContext.RequestServices.GetService<SessionMultiplexerHub>()?.TouchSubscribedAgent(agentId ?? "mons");
                IReadOnlyList<QwenPawChatDto>? chats;
                try
                {
                    var chatsJson = await client.GetAgentChatsRawAsync(agentId ?? "mons", cancellationToken);
                    var chatScope = await Agent360.API.Features.Agents.Security.ChatVisibilityGuard.ResolveScopeAsync(agentId ?? "mons", httpContext, cancellationToken);
                    chatsJson = Agent360.API.Features.Agents.Security.ChatVisibilityGuard.FilterChatsJson(chatsJson, chatScope);
                    chats = string.IsNullOrWhiteSpace(chatsJson)
                        ? Array.Empty<QwenPawChatDto>()
                        : System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<QwenPawChatDto>>(chatsJson);
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
                    // QwenPaw's chat status is authoritative: a new turn can start within a second of the previous one.
                    bool rawRunning = string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Status, "in_progress", StringComparison.OrdinalIgnoreCase);

                    var isRunning = queueManager.IsSessionTurnActive(id)
                        || (!string.IsNullOrWhiteSpace(c.SessionId) && queueManager.IsSessionTurnActive(c.SessionId))
                        || rawRunning;

                    var isUnread = ChannelResponseStore.IsUnread(id)
                        || (!string.IsNullOrWhiteSpace(c.SessionId) && ChannelResponseStore.IsUnread(c.SessionId));

                    var cleanName = c.Name;
                    if (!string.IsNullOrEmpty(cleanName))
                    {
                        var ctxIdx = cleanName.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                        if (ctxIdx >= 0) cleanName = cleanName[..ctxIdx].TrimEnd();
                        cleanName = System.Text.RegularExpressions.Regex.Replace(cleanName, @"^\[Replying to [^:\]]+:\s*""[^""]*""\]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    }

                    return new AgentSessionResponse(
                        Id: id,
                        Title: string.IsNullOrWhiteSpace(cleanName) ? "Session" : cleanName,
                        UpdatedAt: c.UpdatedAt,
                        MessageCount: 1,
                        LastMessageSnippet: cleanName,
                        Unread: isUnread,
                        SessionId: c.SessionId,
                        Channel: c.Channel,
                        Status: isRunning ? "running" : (c.Status ?? "idle"),
                        IsRunning: isRunning,
                        Pinned: c.Pinned,
                        Archived: c.Archived
                    );
                }).ToList();

                return TypedResults.Ok(result);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all agent chats/sessions")
            .WithDescription("Retrieves the sessions and recent chat threads mapped from AgentCore /api/chats.")
            .Produces<IReadOnlyList<AgentSessionResponse>>()
            .AllowAnonymous();

        // Mark session as read
        routeBuilder.MapPost(
            "chats/{chatId}/read",
            async (string chatId, [FromQuery] string? agentId, HttpContext httpContext, IFleetInstanceRoutingService routing, IQwenPawClient qwenPawClient, CancellationToken ct) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, ct) is { } denied) return denied;
                if (await Agent360.API.Features.Agents.Security.ChatVisibilityGuard.EnforceChatAsync(agentId ?? "mons", chatId, httpContext, qwenPawClient, ct) is { } hidden) return hidden;
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
            async (string agentId, string chatId, HttpContext httpContext, IFleetInstanceRoutingService routing, IQwenPawClient qwenPawClient, CancellationToken ct) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
                if (await Agent360.API.Features.Agents.Security.ChatVisibilityGuard.EnforceChatAsync(agentId, chatId, httpContext, qwenPawClient, ct) is { } hidden) return hidden;
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

        // Update chat title / name / pinned state (Synchronizes to QwenPaw runtime and Matrix Session Room)
        static async Task<IResult> ExecuteChatUpdateAsync(
            string agentId,
            string chatId,
            UpdateChatTitleRequest? body,
            HttpContext httpContext,
            IFleetInstanceRoutingService routing,
            IQwenPawClient client,
            CancellationToken ct)
        {
            if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            if (await Agent360.API.Features.Agents.Security.ChatVisibilityGuard.EnforceChatAsync(agentId, chatId, httpContext, client, ct) is { } hidden) return hidden;
            var rawTitle = body?.Title ?? body?.Name;
            var hasTitle = !string.IsNullOrWhiteSpace(rawTitle);
            var hasPinned = body?.Pinned.HasValue == true;
            var hasArchived = body?.Archived.HasValue == true;
            if (!hasTitle && !hasPinned && !hasArchived)
            {
                return Results.Problem(detail: "Either title, pinned, or archived is required", statusCode: 400, title: "Invalid request");
            }
            var cleanTitle = hasTitle ? rawTitle!.Trim() : null;

            var target = routing.ResolveTarget(agentId);
            if (target.Instance is null)
            {
                return Results.NotFound(new { message = $"Agent instance '{agentId}' not found" });
            }

            // 1. Resolve UUID if chatId is session_id, and fetch existing name/pinned if only one was provided
            string targetChatId = chatId;
            string? existingName = null;
            bool? existingPinned = null;
            try
            {
                using var listResp = await routing.SendAsync(
                    target.Instance,
                    HttpMethod.Get,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/chats",
                    agentProfileHeader: target.TargetAgentProfile,
                    ct: ct);

                if (listResp.IsSuccessStatusCode)
                {
                    var chats = await listResp.Content.ReadFromJsonAsync<IReadOnlyList<QwenPawChatDto>>(ct);
                    var matched = chats?.FirstOrDefault(c =>
                        string.Equals(c.Id, chatId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.SessionId, chatId, StringComparison.OrdinalIgnoreCase));

                    if (matched is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(matched.Id)) targetChatId = matched.Id;
                        existingName = matched.Name;
                        existingPinned = matched.Pinned;
                    }
                }
            }
            catch { }

            // 2. Push update directly to QwenPaw runtime via PUT /api/agents/{profile}/chats/{targetChatId}
            var qpPayload = new Dictionary<string, object?>();
            if (hasTitle) qpPayload["name"] = cleanTitle;
            if (hasPinned) qpPayload["pinned"] = body!.Pinned!.Value;
            if (hasArchived) qpPayload["archived"] = body!.Archived!.Value;

            HttpResponseMessage? qpResp = null;
            try
            {
                qpResp = await routing.SendAsync(
                    target.Instance,
                    HttpMethod.Put,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/chats/{Uri.EscapeDataString(targetChatId)}",
                    () => JsonContent.Create(qpPayload),
                    agentProfileHeader: target.TargetAgentProfile,
                    timeout: TimeSpan.FromSeconds(10),
                    ct: ct);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: $"QwenPaw runtime update failed: {ex.Message}", statusCode: 502);
            }

            using (qpResp)
            {
                if (qpResp is not null && !qpResp.IsSuccessStatusCode && qpResp.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    var errText = await qpResp.Content.ReadAsStringAsync(ct);
                    return Results.Problem(detail: errText, statusCode: (int)qpResp.StatusCode);
                }
            }

            // Invalidate Agent360 cached chat list so next GET /chats returns the updated name/pin state
            client.InvalidateChatList(agentId);

            // 3. Also sync Matrix session collaboration room title ONLY if an active multi-agent room already exists for this chat
            if (hasTitle && !string.IsNullOrWhiteSpace(cleanTitle))
            {
                try
                {
                    var roomStore = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
                    var existingRoom = roomStore?.GetByChat(agentId, chatId);
                    if (Agent360.API.Features.Agents.SubAgents.SessionRoomStore.IsMultiAgentRoom(existingRoom))
                    {
                        var orchestrator = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomOrchestrator>();
                        if (orchestrator is not null)
                        {
                            await orchestrator.SyncRoomTitleAsync(agentId, chatId, cleanTitle, ct);
                        }
                    }
                }
                catch { }
            }

            var finalTitle = cleanTitle ?? existingName ?? "Cuộc hội thoại";
            var finalPinned = body?.Pinned ?? existingPinned ?? false;

            var multiplexerHub = httpContext.RequestServices.GetService<SessionMultiplexerHub>();
            if (multiplexerHub is not null)
            {
                var normAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
                _ = multiplexerHub.BroadcastAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "chat_session_upsert",
                    agent_id = normAgent,
                    is_new = false,
                    session = new
                    {
                        id = targetChatId,
                        session_id = chatId,
                        title = finalTitle,
                        name = finalTitle,
                        pinned = finalPinned,
                        updated_at = DateTimeOffset.UtcNow.ToString("O")
                    },
                    timestamp = DateTimeOffset.UtcNow.ToString("O")
                }));
            }

            return TypedResults.Ok(new
            {
                success = true,
                agent_id = agentId,
                chat_id = chatId,
                resolved_chat_id = targetChatId,
                title = finalTitle,
                pinned = finalPinned
            });
        }

        routeBuilder.MapMethods(
            "agents/{agentId}/chats/{chatId}",
            ["PUT", "PATCH"],
            (
                string agentId,
                string chatId,
                [FromBody] UpdateChatTitleRequest? body,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                IQwenPawClient client,
                CancellationToken ct) => ExecuteChatUpdateAsync(agentId, chatId, body, httpContext, routing, client, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("UpdateAgentChatTitle")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Update chat title / name / pinned state across QwenPaw runtime and session room")
            .AllowAnonymous();

        routeBuilder.MapMethods(
            "agents/{agentId}/chats/{chatId}/pin",
            ["PUT", "PATCH", "POST"],
            (
                string agentId,
                string chatId,
                [FromBody] UpdateChatTitleRequest? body,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                IQwenPawClient client,
                CancellationToken ct) => ExecuteChatUpdateAsync(agentId, chatId, body, httpContext, routing, client, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("PinAgentChat")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Pin or unpin a chat session on QwenPaw runtime")
            .AllowAnonymous();

        // Convenience route: PUT/PATCH /api/v1/chats/{chatId}
        routeBuilder.MapMethods(
            "chats/{chatId}",
            ["PUT", "PATCH"],
            (
                string chatId,
                [FromQuery] string? agentId,
                [FromBody] UpdateChatTitleRequest? body,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                IQwenPawClient client,
                CancellationToken ct) => ExecuteChatUpdateAsync(string.IsNullOrWhiteSpace(agentId) ? "mons" : agentId, chatId, body, httpContext, routing, client, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("UpdateChatTitle")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Update chat title or pinned state by chatId and optional agentId")
            .AllowAnonymous();

        // Delete chat session on QwenPaw runtime
        static async Task<IResult> ExecuteChatDeleteAsync(
            string agentId,
            string chatId,
            HttpContext httpContext,
            IFleetInstanceRoutingService routing,
            IQwenPawClient client,
            CancellationToken ct)
        {
            if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            if (await Agent360.API.Features.Agents.Security.ChatVisibilityGuard.EnforceChatAsync(agentId, chatId, httpContext, client, ct) is { } hidden) return hidden;
            var target = routing.ResolveTarget(agentId);
            if (target.Instance is null)
            {
                return Results.NotFound(new { message = $"Agent instance {agentId} not found" });
            }

            string targetChatId = chatId;
            try
            {
                using var listResp = await routing.SendAsync(
                    target.Instance,
                    HttpMethod.Get,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/chats",
                    agentProfileHeader: target.TargetAgentProfile,
                    ct: ct);

                if (listResp.IsSuccessStatusCode)
                {
                    var chats = await listResp.Content.ReadFromJsonAsync<IReadOnlyList<QwenPawChatDto>>(ct);
                    var matched = chats?.FirstOrDefault(c =>
                        string.Equals(c.Id, chatId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.SessionId, chatId, StringComparison.OrdinalIgnoreCase));

                    if (matched is not null && !string.IsNullOrWhiteSpace(matched.Id))
                    {
                        targetChatId = matched.Id;
                    }
                }
            }
            catch { }

            try
            {
                using var delResp = await routing.SendAsync(
                    target.Instance,
                    HttpMethod.Delete,
                    $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/chats/{Uri.EscapeDataString(targetChatId)}",
                    agentProfileHeader: target.TargetAgentProfile,
                    timeout: TimeSpan.FromSeconds(10),
                    ct: ct);
            }
            catch { }

            client.InvalidateChatList(agentId);
            return TypedResults.Ok(new { success = true, agent_id = agentId, chat_id = chatId, deleted_chat_id = targetChatId });
        }

        routeBuilder.MapDelete(
            "agents/{agentId}/chats/{chatId}",
            (
                string agentId,
                string chatId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                IQwenPawClient client,
                CancellationToken ct) => ExecuteChatDeleteAsync(agentId, chatId, httpContext, routing, client, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("DeleteAgentChat")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Delete a chat session from QwenPaw runtime")
            .AllowAnonymous();

        routeBuilder.MapDelete(
            "chats/{chatId}",
            (
                string chatId,
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                IQwenPawClient client,
                CancellationToken ct) => ExecuteChatDeleteAsync(string.IsNullOrWhiteSpace(agentId) ? "mons" : agentId, chatId, httpContext, routing, client, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("DeleteChat")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Delete a chat session by chatId and optional agentId")
            .AllowAnonymous();
    }
}
