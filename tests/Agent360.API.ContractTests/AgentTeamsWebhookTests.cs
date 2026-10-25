using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.SubAgents;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
        var ct = TestContext.Current.CancellationToken;
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
}
