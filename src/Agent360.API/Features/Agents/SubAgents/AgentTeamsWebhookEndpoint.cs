using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Analytics;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.SubAgents;
public sealed record AgentTeamsInboundWebhookRequest(
    [property: JsonPropertyName("event_id")] string? EventId,
    [property: JsonPropertyName("type")] string? Type, // "m.room.message" | "task_status" | "task_result"
    [property: JsonPropertyName("room_id")] string? RoomId,
    [property: JsonPropertyName("matrix_room_id")] string? MatrixRoomId,
    [property: JsonPropertyName("sender")] string? Sender,
    [property: JsonPropertyName("child_agent_ref")] string? ChildAgentRef,
    [property: JsonPropertyName("author_id")] string? AuthorId,
    [property: JsonPropertyName("author_name")] string? AuthorName,
    [property: JsonPropertyName("author_role")] string? AuthorRole, // "host" | "collaborator" | "manager" | "admin"
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("content")] JsonElement? Content,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("timestamp")] string? Timestamp);

public sealed class AgentTeamsWebhookEndpoint : IEndpoint
{
    private const string WebhookSignatureHeader = "X-AgentTeams-Signature";
    private const string WebhookTimestampHeader = "X-AgentTeams-Timestamp";
    private const string DeduplicationCachePrefix = "agentteams:webhook:event:";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost("webhooks/agentteams/events", async (
            HttpContext context,
            [FromServices] SessionRoomStore roomStore,
            [FromServices] IConfiguration config,
            [FromServices] IMemoryCache cache,
            [FromServices] ILogger<AgentTeamsWebhookEndpoint> logger,
            CancellationToken ct) =>
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(ct);
            context.Request.Body.Position = 0;

            // 1. Verify HMAC Signature if configured
            var secret = config["AGENTTEAMS_CALLBACK_SECRET"]
                ?? Environment.GetEnvironmentVariable("A2A_CALLBACK_SECRET")
                ?? config["A2A_CALLBACK_SECRET"];

            if (!string.IsNullOrWhiteSpace(secret))
            {
                if (!context.Request.Headers.TryGetValue(WebhookTimestampHeader, out var tsVal) ||
                    !context.Request.Headers.TryGetValue(WebhookSignatureHeader, out var sigVal))
                {
                    logger.LogWarning("AgentTeams webhook rejected: missing timestamp or signature header");
                    return Results.Unauthorized();
                }

                if (!long.TryParse(tsVal, out var tsUnix) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tsUnix) > 300)
                {
                    logger.LogWarning("AgentTeams webhook rejected: stale timestamp {Ts}", tsVal.ToString());
                    return Results.Unauthorized();
                }

                var rawBytes = Encoding.UTF8.GetBytes(tsVal + "." + rawBody);
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var computedHash = Convert.ToHexString(hmac.ComputeHash(rawBytes)).ToLowerInvariant();
                var expectedSig = sigVal.ToString().Replace("sha256=", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedHash), Encoding.UTF8.GetBytes(expectedSig)))
                {
                    logger.LogWarning("AgentTeams webhook rejected: invalid signature");
                    return Results.Unauthorized();
                }
            }

            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return Results.BadRequest(new { error = "empty_payload" });
            }

            AgentTeamsInboundWebhookRequest? req;
            try
            {
                req = JsonSerializer.Deserialize<AgentTeamsInboundWebhookRequest>(rawBody);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AgentTeams webhook JSON deserialization failed");
                return Results.BadRequest(new { error = "invalid_json" });
            }

            if (req is null)
            {
                return Results.BadRequest(new { error = "null_request" });
            }

            // 2. Deduplication check
            var eventId = req.EventId ?? req.CorrelationId;
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                var dedupeKey = DeduplicationCachePrefix + eventId;
                if (cache.TryGetValue(dedupeKey, out _))
                {
                    logger.LogInformation("AgentTeams webhook duplicate event dropped: {EventId}", eventId);
                    return Results.Ok(new { status = "duplicate", event_id = eventId });
                }
                cache.Set(dedupeKey, true, TimeSpan.FromMinutes(10));
            }

            // 3. Resolve Target Room by RoomId or MatrixRoomId
            var targetRoomId = req.RoomId ?? req.MatrixRoomId;
            if (string.IsNullOrWhiteSpace(targetRoomId))
            {
                return Results.BadRequest(new { error = "missing_room_id" });
            }

            var room = roomStore.GetByRoomId(targetRoomId);
            if (room is null)
            {
                logger.LogWarning("AgentTeams webhook: no session room matched target room ID {RoomId}", targetRoomId);
                return Results.NotFound(new { error = "room_not_found", room_id = targetRoomId });
            }

            // 4. Extract message text
            var messageText = req.Text;
            if (string.IsNullOrWhiteSpace(messageText) && req.Content.HasValue)
            {
                if (req.Content.Value.ValueKind == JsonValueKind.Object &&
                    req.Content.Value.TryGetProperty("body", out var bProp))
                {
                    messageText = bProp.GetString();
                }
                else if (req.Content.Value.ValueKind == JsonValueKind.String)
                {
                    messageText = req.Content.Value.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(messageText))
            {
                logger.LogInformation("AgentTeams webhook event {Type} received without text content, acknowledged.", req.Type);
                return Results.Ok(new { status = "acknowledged_no_text", event_id = eventId });
            }

            // 5. Ingest and project turn into Room and live Session
            var author = SubAgentService.NormalizeAgentId(req.AuthorId ?? req.Sender ?? "collaborator");
            var authorRole = req.AuthorRole ?? "collaborator";
            var turnKind = req.Type switch
            {
                "task_result" => "collaborator_reply",
                "task_status" => "collaborator_progress",
                _ => "collaborator_reply"
            };

            var turnId = $"wt_{Guid.NewGuid():N}"[..12];
            var now = DateTimeOffset.UtcNow;
            var turn = new SessionRoomTurn(
                turnId,
                room.Turns.Count + 1,
                author,
                req.AuthorName ?? author,
                authorRole,
                turnKind,
                messageText.Trim(),
                room.HostAgentId,
                now);

            var updatedRoom = await roomStore.SaveAsync(room with
            {
                Status = "completed",
                CurrentTurnCount = room.Turns.Count + 1,
                Turns = [.. room.Turns, turn],
                UpdatedAt = now
            });

            // Broadcast to ChannelResponseStore so live SSE listeners receive it immediately
            var metadataJson = JsonSerializer.Serialize(new
            {
                room_id = updatedRoom.RoomId,
                matrix_room_id = updatedRoom.MatrixRoomId,
                element_room_url = updatedRoom.ElementRoomUrl,
                sender_agent_id = turn.AuthorId,
                sender_agent_name = turn.AuthorName,
                author_role = turn.AuthorRole,
                turn_kind = turn.Kind,
                turn_no = turn.TurnNo,
                target_agent_id = turn.TargetAgentId
            });

            ChannelResponseStore.Record(
                agentId: updatedRoom.HostAgentId,
                sessionId: updatedRoom.HostChatId,
                channel: "agentteams-room",
                role: "assistant",
                content: turn.Text,
                status: "completed",
                id: turn.Id,
                metadata: metadataJson);

            if (!string.Equals(updatedRoom.HostSessionId, updatedRoom.HostChatId, StringComparison.OrdinalIgnoreCase))
            {
                ChannelResponseStore.Record(
                    agentId: updatedRoom.HostAgentId,
                    sessionId: updatedRoom.HostSessionId,
                    channel: "agentteams-room",
                    role: "assistant",
                    content: turn.Text,
                    status: "completed",
                    id: turn.Id,
                    metadata: metadataJson);
            }

            logger.LogInformation("AgentTeams webhook turn ingested into room {RoomId} turn {TurnNo} from {Author}", updatedRoom.RoomId, turn.TurnNo, author);
            return Results.Ok(new
            {
                status = "processed",
                event_id = eventId,
                room_id = updatedRoom.RoomId,
                turn_id = turn.Id
            });
        })
        .WithName("AgentTeamsWebhookEvents")
        .WithTags(EndpointTags.Agents)
        .MapToApiVersion(ApiVersion.Default)
        .WithSummary("Inbound webhook receiver for AgentTeams and Matrix room events with HMAC validation and deduplication")
        .AllowAnonymous();
    }
}
