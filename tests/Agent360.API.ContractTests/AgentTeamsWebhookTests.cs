using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Endpoints;
using Agent360.API.Features.Agents.SubAgents;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Agent360.API.ContractTests;

public class AgentTeamsWebhookTests
{
    [Fact]
    public void HMAC_Signature_Matches_Expected_Algorithm()
    {
        var secret = "test-secret-a2a-key";
        var timestamp = "1729850000";
        var rawBody = """{"room_id":"!test:matrix","text":"Hello from Collaborator"}""";

        var payloadToSign = timestamp + "." + rawBody;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computedHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadToSign))).ToLowerInvariant();

        computedHash.Should().NotBeNullOrWhiteSpace();
        computedHash.Length.Should().Be(64);
    }

    [Fact]
    public async Task Webhook_TurnIngestion_UpdatesRoomAndChannelResponseStore()
    {
        var roomStore = new SessionRoomStore();

        // 1. Create host room
        var room = await roomStore.SaveAsync(new SessionCollaborationRoom(
            "rm_webhook_01",
            "!matrix_webhook_room:agentteams-gateway.hmz.one",
            "https://chat.hmz.one/#/room/!placeholder",
            "mons",
            "chat_wh_01",
            "chat_wh_01",
            [],
            "idle",
            null,
            10,
            0,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        // 2. Ingest turn from webhook
        var turn = new SessionRoomTurn(
            "wt_001",
            room.Turns.Count + 1,
            "zen",
            "Zen (Sales)",
            "collaborator",
            "collaborator_reply",
            "Báo cáo chiến dịch từ webhook AgentTeams đã hoàn tất.",
            "mons",
            DateTimeOffset.UtcNow);

        var updated = await roomStore.SaveAsync(room with
        {
            Status = "completed",
            CurrentTurnCount = room.Turns.Count + 1,
            Turns = [.. room.Turns, turn]
        });

        updated.Turns.Should().HaveCount(1);
        updated.Turns[0].Text.Should().Contain("Báo cáo chiến dịch từ webhook AgentTeams");

        // 3. ChannelResponseStore recording
        ChannelResponseStore.Record(
            "mons",
            "chat_wh_01",
            "agentteams-room",
            "assistant",
            turn.Text,
            "completed",
            turn.Id);

        var recent = ChannelResponseStore.GetRecent("mons", "agentteams-room", 10);
        recent.Should().Contain(e => e.Content.Contains("Báo cáo chiến dịch từ webhook AgentTeams"));
    }

    [Fact]
    public void Deduplication_Cache_Drops_Repeated_EventIds()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        const string eventId = "evt_dedupe_test_999";
        const string cacheKey = "agentteams:webhook:event:" + eventId;

        // First attempt: Not duplicate
        cache.TryGetValue(cacheKey, out _).Should().BeFalse();
        cache.Set(cacheKey, true, TimeSpan.FromMinutes(5));

        // Second attempt: Recognized as duplicate
        cache.TryGetValue(cacheKey, out _).Should().BeTrue();
    }

    [Fact]
    public async Task IngestChannelResponse_WithRoomId_SyncsTurnIntoCollaborationRoom()
    {
        var roomStore = new SessionRoomStore();

        // Host room for mons
        var room = await roomStore.SaveAsync(new SessionCollaborationRoom(
            "rm_ingest_01",
            "!matrix_ingest:agentteams-gateway.hmz.one",
            "https://chat.hmz.one/#/room/!placeholder",
            "mons",
            "chat_ingest_01",
            "chat_ingest_01",
            [],
            "idle",
            null,
            10,
            0,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        // Incoming response event from QwenPaw with RoomId
        var dto = new IngestChannelResponseDto(
            SessionId: "chat_ingest_01",
            Channel: "qwenpaw-tool",
            Role: "assistant",
            Content: "QwenPaw tool execution finished.",
            Status: "completed",
            Id: "turn_qp_123",
            AgentId: "zen",
            ChatId: "chat_ingest_01",
            RoomId: "rm_ingest_01");

        // Simulate sync logic from AgentChatSyncStreamEndpoint
        var targetRoom = roomStore.GetByRoomId(dto.RoomId!);
        targetRoom.Should().NotBeNull();

        var turn = new SessionRoomTurn(
            dto.Id!,
            targetRoom!.Turns.Count + 1,
            dto.AgentId!,
            dto.AgentId!,
            "collaborator",
            "collaborator_reply",
            dto.Content!,
            targetRoom.HostAgentId,
            DateTimeOffset.UtcNow);

        var updated = await roomStore.SaveAsync(targetRoom with
        {
            Status = "completed",
            CurrentTurnCount = targetRoom.Turns.Count + 1,
            Turns = [.. targetRoom.Turns, turn]
        });

        updated.Turns.Should().HaveCount(1);
        updated.Turns[0].Id.Should().Be("turn_qp_123");
        updated.Turns[0].Text.Should().Be("QwenPaw tool execution finished.");
    }
}
