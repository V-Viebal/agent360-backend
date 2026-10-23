using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Features.Agents.SubAgents;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class FakeRoomA2AClient : IQwenPawA2AClient
{
    public string NextReply { get; set; } = "Kết quả phân tích từ Agent B trong Room.";

    public Task<IReadOnlyList<NativeAgentInfo>> ListAgentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NativeAgentInfo>>(
        [
            new NativeAgentInfo("mons", "Mons", true),
            new NativeAgentInfo("zen", "Zen", true),
            new NativeAgentInfo("ken", "Ken", true)
        ]);

    public Task<NativeChatResult> ChatAsync(string fromAgent, string toAgent, string text, int timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(new NativeChatResult(true, $"room:{fromAgent}:{toAgent}", NextReply, null, 200));

    public Task<NativeChatResult> ChatInSessionAsync(string fromAgent, string toAgent, string text, string? sessionId, int timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(new NativeChatResult(true, sessionId ?? $"room:{fromAgent}:{toAgent}", NextReply, null, 200));

    public Task<NativeSubmitResult> SubmitAsync(string fromAgent, string toAgent, string text, int timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(new NativeSubmitResult(true, $"room:{fromAgent}:{toAgent}", "task_1", timeoutSeconds, null, 200));

    public Task<NativeTaskStatus> GetTaskAsync(string nativeTaskId, string? toAgent, CancellationToken ct) =>
        Task.FromResult(new NativeTaskStatus("finished", "completed", NextReply, null, "s1", 200));

    public Task<bool> StopChatAsync(string sessionId, string toAgent, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<bool> UpsertSkillAsync(string agentId, string skillName, string content, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<DetectedNativeDelegation>> ScanAgentDelegationsAsync(string agentId, int maxChats, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DetectedNativeDelegation>>([]);
}

public class SessionRoomTests
{
    private static SubAgentStore CreateTempSubAgentStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agent360_substore_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return new SubAgentStore(new FileSubAgentPersistence(dir));
    }

    [Fact]
    public async Task EnsureRoom_CreatesFourCanonicalMembers_AndAutoBindsCollaborator()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient();
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var room = await orchestrator.EnsureRoomAsync("mons", "chat-session-a1", "zen", "sess-a1", ct);

        room.HostAgentId.Should().Be("mons");
        room.HostChatId.Should().Be("chat-session-a1");
        room.MatrixRoomId.Should().StartWith("!room_mons_");
        room.MatrixRoomId.Should().EndWith(":agentteams-gateway.hmz.one");
        room.Participants.Should().HaveCount(4);
        room.Participants.Select(p => p.Role).Should().BeEquivalentTo(["host", "collaborator", "manager", "admin"]);

        var binding = await subStore.GetBindingAsync("mons", "zen");
        binding.Should().NotBeNull();
        binding!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteCollaboratorTurn_ProjectsResponseToHostSession_AndEnforcesManagerLoopGuard()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient { NextReply = "Báo cáo doanh số từ Zen qua Room." };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var (room, turn) = await orchestrator.ExecuteCollaboratorTurnAsync(
            "mons", "chat-session-loop", "zen", "Kiểm tra pipeline bán hàng quý 4", "User đang hỏi về doanh thu.", "sess-loop", ct);

        turn.AuthorId.Should().Be("zen");
        turn.AuthorRole.Should().Be("collaborator");
        turn.Kind.Should().Be("collaborator_reply");
        turn.Text.Should().Contain("Báo cáo doanh số từ Zen qua Room.");
        room.Status.Should().Be("completed");

        // Verify projected onto host chat session in ChannelResponseStore
        var projected = ChannelResponseStore.GetRecentBySession("chat-session-loop", 10);
        projected.Should().Contain(e => e.Channel == "agentteams-room" && e.Content.Contains("Báo cáo doanh số từ Zen qua Room."));

        // Execute 3 more collaborator turns to reach MaxRoomTurns (4)
        await orchestrator.ExecuteCollaboratorTurnAsync("mons", "chat-session-loop", "zen", "Turn 2", null, "sess-loop", ct);
        await orchestrator.ExecuteCollaboratorTurnAsync("mons", "chat-session-loop", "zen", "Turn 3", null, "sess-loop", ct);
        await orchestrator.ExecuteCollaboratorTurnAsync("mons", "chat-session-loop", "zen", "Turn 4", null, "sess-loop", ct);

        // 5th turn should be intercepted by Manager Loop Guard
        var (_, guardTurn) = await orchestrator.ExecuteCollaboratorTurnAsync("mons", "chat-session-loop", "zen", "Turn 5", null, "sess-loop", ct);
        guardTurn.AuthorRole.Should().Be("manager");
        guardTurn.Kind.Should().Be("manager_guard");
        guardTurn.Text.Should().Contain("Manager Loop Guard");
    }

    [Fact]
    public void DetectMentionedCollaborator_FindsTargetAgent_AndIgnoresHostSelfMention()
    {
        SessionRoomOrchestrator.DetectMentionedCollaborator("Nhờ @zen kiểm tra giúp báo giá nhé", "mons").Should().Be("zen");
        SessionRoomOrchestrator.DetectMentionedCollaborator("@mons ơi gọi @ken vào cùng xem", "mons").Should().Be("ken");
        SessionRoomOrchestrator.DetectMentionedCollaborator("Xin chào @mons", "mons").Should().BeNull();
    }
}
