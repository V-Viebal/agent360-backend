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
    public Queue<string> ReplyQueue { get; } = new();

    public Task<IReadOnlyList<NativeAgentInfo>> ListAgentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NativeAgentInfo>>(
        [
            new NativeAgentInfo("mons", "Mons", true),
            new NativeAgentInfo("zen", "Zen", true),
            new NativeAgentInfo("ken", "Ken", true)
        ]);

    public Task<NativeChatResult> ChatAsync(string fromAgent, string toAgent, string text, int timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(new NativeChatResult(true, $"room:{fromAgent}:{toAgent}", ReplyQueue.Count > 0 ? ReplyQueue.Dequeue() : NextReply, null, 200));

    public string? LastMessageSent { get; private set; }

    public Task<NativeChatResult> ChatInSessionAsync(string fromAgent, string toAgent, string text, string? sessionId, int timeoutSeconds, CancellationToken ct)
    {
        LastMessageSent = text;
        return Task.FromResult(new NativeChatResult(true, sessionId ?? $"room:{fromAgent}:{toAgent}", ReplyQueue.Count > 0 ? ReplyQueue.Dequeue() : NextReply, null, 200));
    }

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
        room.MatrixRoomId.Should().StartWith("!");
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

    [Fact]
    public async Task NewChatSession_CreatesRoomWithScopedHostAgent_AndDefaultManagerAndAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient();
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        // 1. Brand-new chat session with only Host Agent (mons) in scope -> defaults include manager and admin
        var newSessionId = "session-new-889900";
        var initialRoom = await orchestrator.EnsureRoomAsync("mons", newSessionId, null, newSessionId, ct);

        initialRoom.HostAgentId.Should().Be("mons");
        initialRoom.HostChatId.Should().Be(newSessionId);
        initialRoom.Participants.Select(p => p.Role).Should().BeEquivalentTo(["host", "manager", "admin"]);
        initialRoom.Participants.Select(p => p.AgentId).Should().BeEquivalentTo(["mons", "agentteams-manager", "admin"]);

        // 2. When collaborator zen is pulled into the session scope -> room now has mons, zen, manager, admin
        var expandedRoom = await orchestrator.EnsureRoomAsync("mons", newSessionId, "zen", newSessionId, ct);
        expandedRoom.Participants.Select(p => p.AgentId).Should().BeEquivalentTo(["mons", "zen", "agentteams-manager", "admin"]);

        // 3. Sync room title from first prompt -> sets RoomTitle and updates Matrix m.room.name
        var titledRoom = await orchestrator.SyncRoomTitleAsync("mons", newSessionId, "Kiểm tra chiến dịch Q4 cùng @zen", ct);
        titledRoom.Should().NotBeNull();
        titledRoom!.RoomTitle.Should().Be("[mons] Kiểm tra chiến dịch Q4 cùng @zen");
    }

    [Fact]
    public void ReactionClassifier_Classifies_Praise_Disapprove_AndBuildsPrompt()
    {
        var (posSent, posCat, posDir) = AgentReactionClassifier.Classify("❤️");
        posSent.Should().Be("positive");
        posCat.Should().Be("praise");

        var (negSent, negCat, negDir) = AgentReactionClassifier.Classify("👎");
        negSent.Should().Be("negative");
        negCat.Should().Be("disapprove");

        var prompt = AgentReactionClassifier.BuildImplicitReactionPrompt("👎", "Câu trả lời trước đó của agent", "Mons");
        prompt.Should().Contain("[IMPLICIT_REACTION_FEEDBACK");
        prompt.Should().Contain("emoji=\"👎\"");
        prompt.Should().Contain("category=\"disapprove\"");
        prompt.Should().Contain("Mons");
        prompt.Should().Contain("[/IMPLICIT_REACTION_FEEDBACK]");
    }

    [Fact]
    public async Task RecordReaction_PersistsAndSummarizes_AndEnrichAttachesReactionsAndFiltersFeedback()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();

        // 1. Record reactions on msg_01
        var room = await roomStore.RecordReactionAsync("mons", "chat_react_01", "msg_01", "❤️", "user_1", "add", ct);
        room = await roomStore.RecordReactionAsync("mons", "chat_react_01", "msg_01", "🔥", "user_1", "add", ct);
        room = await roomStore.RecordReactionAsync("mons", "chat_react_01", "msg_02", "👎", "user_2", "add", ct);

        room.Reactions.Should().NotBeNull();
        room.Reactions!["msg_01"].Should().HaveCount(2);
        room.Reactions!["msg_02"].Should().HaveCount(1);

        // 2. Summary test
        var summary = roomStore.GetReactionSummary("mons");
        summary.TotalReactions.Should().Be(3);
        summary.PositiveCount.Should().Be(2);
        summary.NegativeCount.Should().Be(1);
        summary.TopEmojis["❤️"].Should().Be(1);

        // 3. Test EnrichChatHistoryJson attaches reactions AND filters out implicit feedback user turn
        var rawChatHistory = """
        {
          "messages": [
            {
              "id": "msg_01",
              "role": "assistant",
              "content": [{"type": "text", "text": "Phản hồi mẫu từ trợ lý."}]
            },
            {
              "id": "msg_implicit_feedback",
              "role": "user",
              "content": [{"type": "text", "text": "[IMPLICIT_REACTION_FEEDBACK emoji=\"❤️\"]\nĐánh giá ngầm...\n[/IMPLICIT_REACTION_FEEDBACK]"}]
            },
            {
              "id": "msg_02",
              "role": "assistant",
              "content": [{"type": "text", "text": "Cảm ơn bạn rất nhiều!"}]
            }
          ]
        }
        """;

        var enriched = roomStore.EnrichChatHistoryJson(rawChatHistory, "mons", "chat_react_01");
        enriched.Should().NotContain("msg_implicit_feedback");
        enriched.Should().NotContain("[IMPLICIT_REACTION_FEEDBACK");
        enriched.Should().Contain("reactions");
        enriched.Should().Contain("reactions");
        enriched.Should().Contain("msg_01");
        enriched.Should().Contain("category");
    }

    [Fact]
    public async Task MultiAgentChatSession_StrictlyMaintainsSingleRoom_AcrossSequentialAndParallelPullsAndSessionAliases()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient();
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var tempSessionId = "session-1760855134832-init";
        var realChatId = "eb74fb84-d319-46fc-83ef-06c306ad4d51";

        // 1. Room initially created with temp session ID
        var r1 = await orchestrator.EnsureRoomAsync("gm-boc", tempSessionId, "zen", tempSessionId, ct);

        // 2. Subsequent parallel pulls using real UUID chatId + tempSessionId for multiple collaborators
        var parallelTasks = new[]
        {
            orchestrator.EnsureRoomAsync("gm-boc", realChatId, "climax-default", tempSessionId, ct),
            orchestrator.EnsureRoomAsync("gm-boc", realChatId, "planthy-pm", tempSessionId, ct),
            orchestrator.EnsureRoomAsync("gm-boc", realChatId, "ken", tempSessionId, ct, ["zen", "climax-default"])
        };
        await Task.WhenAll(parallelTasks);

        // 3. Strictly 1 single room must exist for this chat session
        var allHostRooms = roomStore.ListByHostAgent("gm-boc");
        allHostRooms.Should().HaveCount(1);

        var singleRoom = allHostRooms[0];
        singleRoom.RoomId.Should().Be(r1.RoomId);
        singleRoom.HostChatId.Should().Be(realChatId);
        singleRoom.HostSessionId.Should().Be(tempSessionId);

        // Lookup by either UUID chatId or temp sessionId returns the exact same single Room
        roomStore.GetByChat("gm-boc", realChatId)!.RoomId.Should().Be(singleRoom.RoomId);
        roomStore.GetByChat("gm-boc", tempSessionId)!.RoomId.Should().Be(singleRoom.RoomId);

        // All 4 collaborators + host + manager + admin are in that 1 Room
        singleRoom.Participants.Select(p => p.AgentId).Should().BeEquivalentTo(
            ["gm-boc", "zen", "climax-default", "planthy-pm", "ken", "agentteams-manager", "admin"]);
    }

    [Fact]
    public async Task HostAutonomousTriage_SelectivelyPullsMatchingCollaboratorsFromRoom_OrHandlesSolo()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient();
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "eb74fb84-d319-46fc-83ef-06c306ad4d51";

        // Case 0: Before any collaborators are in the room, a message without @mention is handled solo by Host
        var preRoomDecision = orchestrator.DecideRoomCollaboratorsForTurn("gm-boc", chatId, chatId, "Xin chào anh cần hỏi về quy trình");
        preRoomDecision.ShouldUseRoom.Should().BeFalse();
        preRoomDecision.Mode.Should().Be("no_room_collaborators");

        // Pull zen, climax-default, and planthy-pm into the session's single Room
        await orchestrator.EnsureRoomAsync("gm-boc", chatId, "zen", chatId, ct, ["climax-default", "planthy-pm"]);

        // Case 1: Sales/pricing follow-up without @mention -> Host selectively pulls ONLY zen
        var salesDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Lên giúp anh bảng báo giá bán, chính sách chiết khấu đại lý B2B và điều khoản hợp đồng nhé");
        salesDecision.ShouldUseRoom.Should().BeTrue();
        salesDecision.Mode.Should().Be("specialty_triage");
        salesDecision.SelectedCollaborators.Should().Equal(["zen"]);

        // Case 2: Brand/marketing follow-up without @mention -> Host selectively pulls ONLY climax-default
        var marketingDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Định vị thương hiệu và chiến dịch truyền thông viral trên TikTok nên đi theo concept nào?");
        marketingDecision.ShouldUseRoom.Should().BeTrue();
        marketingDecision.Mode.Should().Be("specialty_triage");
        marketingDecision.SelectedCollaborators.Should().Equal(["climax-default"]);

        // Case 3: Project timeline/roadmap follow-up without @mention -> Host selectively pulls ONLY planthy-pm
        var pmDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Lập lộ trình timeline triển khai và phân công task theo từng tuần trong 2 tháng tới");
        pmDecision.ShouldUseRoom.Should().BeTrue();
        pmDecision.Mode.Should().Be("specialty_triage");
        pmDecision.SelectedCollaborators.Should().Equal(["planthy-pm"]);

        // Case 4: Multi-domain question covering sales + marketing -> Host pulls both zen and climax-default
        var multiDomainDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Phân tích đối thủ về cả chính sách giá bán lẫn chiến dịch truyền thông thương hiệu");
        multiDomainDecision.ShouldUseRoom.Should().BeTrue();
        multiDomainDecision.SelectedCollaborators.Should().BeEquivalentTo(["zen", "climax-default"]);

        // Case 5: Direct Reply to a collaborator bubble or natural name mention without @
        var replyDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "[Replying to 🤖 Climax Default: \"Khung đọc truyền thông...\"]\n\nĐi sâu hơn vào mục số 2 giúp anh nhé");
        replyDecision.ShouldUseRoom.Should().BeTrue();
        replyDecision.Mode.Should().Be("reply_or_named");
        replyDecision.SelectedCollaborators.Should().Equal(["climax-default"]);

        // Case 6: Solo Host directive or brief acknowledgement -> Host handles solo without pushing to Room
        var soloDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Chỉ mình bạn trả lời thôi, tóm tắt lại kết quả trên thành 3 gạch đầu dòng");
        soloDecision.ShouldUseRoom.Should().BeFalse();
        soloDecision.Mode.Should().Be("host_solo");
        soloDecision.SelectedCollaborators.Should().BeEmpty();

        var ackDecision = orchestrator.DecideRoomCollaboratorsForTurn("gm-boc", chatId, chatId, "Ok cảm ơn em nhé!");
        ackDecision.ShouldUseRoom.Should().BeFalse();
        ackDecision.Mode.Should().Be("host_solo");
    }

    [Fact]
    public async Task HostAutonomousTriage_ContinuityFollowup_RoutesToPendingWaveCollaborators_WhenUserSuppliesRequestedContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient
        {
            NextReply = "Chưa đủ dữ liệu. Cần bổ sung: ngành hàng/sản phẩm và tên 2–3 đối thủ cụ thể."
        };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "session-continuity-01";
        await orchestrator.EnsureRoomAsync("gm-boc", chatId, "zen", chatId, ct, ["climax-default", "planthy-pm"]);

        // Wave 1: zen and climax-default run and ask for missing context, followed by Host synthesis
        await orchestrator.ExecuteCollaboratorTurnAsync("gm-boc", chatId, "zen", "@zen @climax-default phân tích các đối thủ", null, chatId, ct);
        await orchestrator.ExecuteCollaboratorTurnAsync("gm-boc", chatId, "climax-default", "@zen @climax-default phân tích các đối thủ", null, chatId, ct);
        await orchestrator.RecordHostSynthesisTurnAsync("gm-boc", chatId, "Anh cho em biết thêm ngành hàng và 2–3 tên đối thủ cụ thể nhé!", null, null, chatId, ct);

        // Wave 2: User replies with raw context WITHOUT @mentions and WITHOUT domain keywords ("sales"/"marketing")
        var followupDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Mảng nội thất văn phòng tại Đà Nẵng, 2 bên kia là Hòa Phát và Xuân Hòa");

        followupDecision.ShouldUseRoom.Should().BeTrue();
        followupDecision.SelectedCollaborators.Should().BeEquivalentTo(["zen", "climax-default"]);
    }

    [Fact]
    public async Task SmoothHostTriage_PrunesPeripheralNoise_SupportsDrillDownAffinity_AndRespectsNegativeExclusions()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient { NextReply = "Phân tích gói giá và kênh bán B2B từ Zen." };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "session-smooth-triage-01";
        await orchestrator.EnsureRoomAsync("gm-boc", chatId, "zen", chatId, ct, ["climax-default", "planthy-pm"]);

        // 1. Dominant Specialty Pruning: Strong commercial/sales query ("báo giá bán", "chiết khấu đại lý B2B", "biên lợi nhuận", "hợp đồng")
        // even if it mentions "thị trường" once -> should prune peripheral climax-default and only pull zen!
        var prunedDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Tính toán bảng báo giá bán, chiết khấu đại lý B2B, biên lợi nhuận và điều khoản hợp đồng trên thị trường này");
        prunedDecision.ShouldUseRoom.Should().BeTrue();
        prunedDecision.Mode.Should().Be("specialty_triage");
        prunedDecision.SelectedCollaborators.Should().Equal(["zen"]);
        prunedDecision.Confidence.Should().BeGreaterThanOrEqualTo(0.85);
        prunedDecision.CollaboratorFocuses.Should().NotBeNull();
        prunedDecision.CollaboratorFocuses!["zen"].Should().Contain("Trọng tâm xử lý");

        // 2. Execute a wave with zen so zen is the active collaborator of the latest wave
        await orchestrator.ExecuteCollaboratorTurnAsync("gm-boc", chatId, "zen", "Tính báo giá B2B", null, chatId, ct);
        await orchestrator.RecordHostSynthesisTurnAsync("gm-boc", chatId, "Đã tổng hợp phương án báo giá B2B từ Zen.", null, null, chatId, ct);

        // 3. Conversational Momentum / Drill-Down Affinity: User asks "Làm rõ hơn mục số 2 và bổ sung thêm ví dụ nhé" (no domain keywords!)
        var drillDownDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Làm rõ hơn mục số 2 và bổ sung thêm ví dụ thực tế nhé");
        drillDownDecision.ShouldUseRoom.Should().BeTrue();
        drillDownDecision.Mode.Should().Be("active_collaborator_affinity");
        drillDownDecision.SelectedCollaborators.Should().Equal(["zen"]);

        // 4. Negative Exclusion: User asks about competitor analysis (which matches both zen and climax-default) but says "không cần gọi zen"
        var exclusionDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Phân tích chiến dịch truyền thông của đối thủ cạnh tranh, không cần gọi zen nhé");
        exclusionDecision.ShouldUseRoom.Should().BeTrue();
        exclusionDecision.SelectedCollaborators.Should().Equal(["climax-default"]);
    }

    [Fact]
    public async Task ExecuteCollaboratorWave_RunsIndependentCollaboratorsInParallel_AndRespectsManualTriagePolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient { NextReply = "Phản hồi chuyên môn song song trong Room." };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "session-parallel-wave-01";
        var completedOrder = new List<string>();

        var (waveRoom, waveTurns) = await orchestrator.ExecuteCollaboratorWaveAsync(
            "gm-boc",
            chatId,
            ["zen", "climax-default", "planthy-pm"],
            "Phân tích toàn diện dự án mới",
            "Ngữ cảnh phiên chat",
            chatId,
            new Dictionary<string, string>
            {
                ["zen"] = "Trọng tâm thương mại",
                ["climax-default"] = "Trọng tâm thương hiệu",
                ["planthy-pm"] = "Trọng tâm tiến độ"
            },
            executionStrategy: "parallel",
            onCollaboratorCompleted: (_, turn, collab) =>
            {
                lock (completedOrder) completedOrder.Add(collab);
                return Task.CompletedTask;
            },
            ct: ct);

        // 1. All 3 collaborators completed in the single Room and streamed via callback
        waveTurns.Should().HaveCount(3);
        completedOrder.Should().BeEquivalentTo(["zen", "climax-default", "planthy-pm"]);
        roomStore.ListByHostAgent("gm-boc").Should().HaveCount(1);
        waveRoom.Participants.Where(p => p.Role == "collaborator").Select(p => p.AgentId)
            .Should().BeEquivalentTo(["zen", "climax-default", "planthy-pm"]);

        // Only 1 user request turn was recorded for the entire wave (plus 1 manager init turn + 3 collaborator replies = 5 turns)
        waveRoom.Turns.Count(t => t.Kind == "request" && t.AuthorRole != "manager").Should().Be(1);
        waveRoom.Turns.Count(t => t.Kind == "collaborator_reply").Should().Be(3);

        // 2. Test TriagePolicy Toggle ("manual" vs "auto")
        var manualRoom = await orchestrator.SetRoomTriagePolicyAsync("gm-boc", chatId, "manual", ct);
        manualRoom.TriagePolicy.Should().Be("manual");

        // In "manual" mode, an un-mentioned domain question stays host_solo
        var manualDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Lên bảng báo giá bán và chiết khấu đại lý B2B");
        manualDecision.ShouldUseRoom.Should().BeFalse();
        manualDecision.Mode.Should().Be("manual_policy_host_solo");

        // Explicit @mention still works in "manual" mode
        var explicitInManual = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "@zen tính lại bảng giá nhé");
        explicitInManual.ShouldUseRoom.Should().BeTrue();
        explicitInManual.SelectedCollaborators.Should().Equal(["zen"]);

        // Switching back to "auto" restores autonomous specialty triage
        await orchestrator.SetRoomTriagePolicyAsync("gm-boc", chatId, "auto", ct);
        var autoDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Lên bảng báo giá bán và chiết khấu đại lý B2B");
        autoDecision.ShouldUseRoom.Should().BeTrue();
        autoDecision.Mode.Should().Be("specialty_triage");
        autoDecision.SelectedCollaborators.Should().Equal(["zen"]);
    }

    [Fact]
    public async Task ManagerRoomGovernance_AuditsEveryMemberTurn_AutoCorrectsDefectiveOutput_AndExcludesStrayMons()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient();
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "session-manager-gov-01";
        var room = await orchestrator.EnsureRoomAsync("gm-boc", chatId, "zen", chatId, ct, ["climax-default"]);

        // 1. Verify Room strictly contains ONLY [gm-boc, zen, climax-default, agentteams-manager, admin] and NO stray "mons" or "mon"
        room.Participants.Select(p => p.AgentId).Should().BeEquivalentTo(
            ["gm-boc", "zen", "climax-default", "agentteams-manager", "admin"]);
        room.Participants.Select(p => p.AgentId).Should().NotContain(["mons", "mon"]);
        room.Participants.Select(p => p.MatrixUserId).Should().NotContain(["@mon:agentteams-gateway.hmz.one", "@mons:agentteams-gateway.hmz.one"]);

        // EnrichChatHistoryJson with null agentId resolves by chatId without falling back to "mons"
        var enrichedRaw = roomStore.EnrichChatHistoryJson("""{"messages":[]}""", null, chatId);
        enrichedRaw.Should().Contain(room.RoomId);

        // 2. Simulate Member Agent (zen) initially returning a defective output (confusing @climax-default as a competitor brand),
        // followed by a corrected response when AgentTeams Manager issues the auto-correction directive!
        a2a.ReplyQueue.Enqueue("Phân tích đối thủ climax-default: hiện chưa rõ dòng sản phẩm.");
        a2a.ReplyQueue.Enqueue("- **So sánh giá & Kênh B2B (Katinat vs Phê La):**\n  + Katinat mạnh về mặt bằng góc & danh mục đồ uống đa dạng.\n  + Phê La tập trung đặc sản Ô Long & định vị ngách rõ rệt.");

        var (updatedRoom, zenTurn) = await orchestrator.ExecuteCollaboratorTurnAsync(
            "gm-boc",
            chatId,
            "zen",
            "@zen @climax-default Ngành hàng là chuỗi cà phê đặc sản, đối thủ chính là Katinat và Phê La",
            null,
            chatId,
            ct);

        zenTurn.ManagerAudit.Should().NotBeNull();
        zenTurn.ManagerAudit!.Passed.Should().BeTrue();
        zenTurn.ManagerAudit.AutoRetried.Should().BeTrue();
        zenTurn.ManagerAudit.Verdict.Should().Be("auto_corrected");
        zenTurn.Text.Should().Contain("Katinat vs Phê La");
        zenTurn.Steps.Should().Contain(s => s.Tool == "manager_quality_gate");

        // 3. Verify BuildManagerSynthesisBrief summarizes audited member outputs for Host synthesis
        var triage = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "@zen @climax-default Ngành hàng là chuỗi cà phê đặc sản, đối thủ chính là Katinat và Phê La");
        var synthesisBrief = orchestrator.BuildManagerSynthesisBrief(
            updatedRoom,
            "Ngành hàng là chuỗi cà phê đặc sản, đối thủ chính là Katinat và Phê La",
            triage,
            [zenTurn]);
        synthesisBrief.Should().Contain("AGENTTEAMS MANAGER");
        synthesisBrief.Should().Contain("AUTO_CORRECTED");
        synthesisBrief.Should().Contain("Đã tự động hiệu chỉnh");
    }

    [Fact]
    public void EnrichChatHistoryJson_RehydratesCollaboratorRepliesFromSharedSessionRoomContext_WhenRoomLedgerIsEmpty()
    {
        var roomStore = new SessionRoomStore();

        var rawHistoryWithEmbeddedContext = """
        {
          "messages": [
            {
              "id": "usr_wave_1",
              "role": "user",
              "type": "message",
              "content": [
                {
                  "type": "text",
                  "text": "@climax-default @zen @planthy-pm phân tích chiến lược bánh mì chay Zeroism\n\n[SHARED SESSION ROOM CONTEXT]\nLưu ý hệ thống: Các thẻ đồng nghiệp AI...\n\nCác chuyên viên AI trong phiên đã lần lượt đóng góp ý kiến chuyên môn (đã qua kiểm định của AgentTeams Manager):\n- @zen (Zen (Sales & Delivery)): Chiến lược thương mại Bánh mì Zeroism giá 89.000đ, biên lợi nhuận 68%.\n- @climax-default (Climax Default): Định vị bánh mì Sài Gòn bản chay vỏ giòn nhân thực vật thật.\n- @planthy-pm (Planthy PM): Lộ trình 90 ngày chuẩn hóa bếp Thảo Điền trước khi mở Quận 3.\nWith vai trò Chủ phòng (@gm-boc), hãy tổng hợp..."
                }
              ]
            },
            {
              "id": "asst_synth_1",
              "role": "assistant",
              "type": "message",
              "content": [
                {
                  "type": "text",
                  "text": "Tổng hợp chiến lược Bánh mì chay Zeroism từ Zen, Climax và Planthy."
                }
              ]
            }
          ]
        }
        """.Replace("With vai trò", "Với vai trò");

        var enriched = roomStore.EnrichChatHistoryJson(rawHistoryWithEmbeddedContext, "gm-boc", "chat-rehydrate-01");
        enriched.Should().NotContain("[SHARED SESSION ROOM CONTEXT]");
        using var doc = System.Text.Json.JsonDocument.Parse(enriched);
        var msgsText = string.Join("\n", doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content")[0].GetProperty("text").GetString()));
        msgsText.Should().Contain("Chiến lược thương mại Bánh mì Zeroism giá 89.000đ");
        msgsText.Should().Contain("Định vị bánh mì Sài Gòn bản chay vỏ giòn nhân thực vật thật.");
        msgsText.Should().Contain("Lộ trình 90 ngày chuẩn hóa bếp Thảo Điền trước khi mở Quận 3.");

        var rehydratedRoom = roomStore.GetByChat("gm-boc", "chat-rehydrate-01");
        rehydratedRoom.Should().NotBeNull();
        rehydratedRoom!.Participants.Where(p => p.Role == "collaborator").Select(p => p.AgentId)
            .Should().BeEquivalentTo(["zen", "climax-default", "planthy-pm"]);
        rehydratedRoom.Turns.Where(t => t.Kind == "collaborator_reply").Should().HaveCount(3);
    }

    [Fact]
    public async Task ManagerIntakeAndRouting_AssignsHandlingAgentsInRoom_AndRecordsManagerTriageStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient { NextReply = "Phân tích gói giá B2B từ Zen." };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "session-manager-intake-01";
        await orchestrator.EnsureRoomAsync("gm-boc", chatId, "zen", chatId, ct, ["climax-default", "planthy-pm"]);

        // 1. New request in existing Room -> AgentTeams Manager (@agentteams-manager) receives and assigns @zen + @gm-boc
        var collabDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Lên bảng báo giá bán và chiết khấu đại lý B2B");
        collabDecision.TriageOwnerId.Should().Be("agentteams-manager");
        collabDecision.ShouldUseRoom.Should().BeTrue();
        collabDecision.SelectedCollaborators.Should().Equal(["zen"]);
        collabDecision.AssignedAgents.Should().Equal(["zen", "gm-boc"]);
        collabDecision.Reason.Should().Contain("AgentTeams Manager (@agentteams-manager)");

        var (roomAfterWave, _) = await orchestrator.ExecuteCollaboratorTurnAsync(
            "gm-boc", chatId, "zen", "Lên bảng báo giá bán và chiết khấu đại lý B2B", null, chatId, ct);
        var reqTurn = roomAfterWave.Turns.First(t => t.Kind == "request" && t.AuthorRole != "manager");
        reqTurn.Steps.Should().NotBeNull();
        reqTurn.Steps!.Should().Contain(s => s.Tool == "agentteams_manager_triage");

        // 2. Solo request in existing Room -> AgentTeams Manager (@agentteams-manager) receives and assigns @gm-boc directly
        var soloDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc", chatId, chatId, "Chỉ mình bạn trả lời thôi, tóm tắt lại kết quả trên");
        soloDecision.TriageOwnerId.Should().Be("agentteams-manager");
        soloDecision.ShouldUseRoom.Should().BeFalse();
        soloDecision.AssignedAgents.Should().Equal(["gm-boc"]);
        soloDecision.Reason.Should().Contain("AgentTeams Manager (@agentteams-manager)");

        var roomAfterSolo = await orchestrator.RecordManagerTriageTurnAsync(
            "gm-boc", chatId, chatId, "Chỉ mình bạn trả lời thôi, tóm tắt lại kết quả trên", soloDecision, ct);
        roomAfterSolo.Should().NotBeNull();
        roomAfterSolo!.Turns.Last().Steps.Should().Contain(s => s.Tool == "agentteams_manager_triage");
    }

    [Fact]
    public async Task ManagerDynamicSpecialistRecruitment_RecruitsExternalSpecialistIntoSingleRoom_WhenDomainIsUncovered()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient { NextReply = "Lộ trình WBS 90 ngày từ Planthy PM." };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "session-manager-recruit-01";
        // Room initially only has zen (Sales) and climax-default (Marketing) — NO planthy-pm yet
        var initialRoom = await orchestrator.EnsureRoomAsync("gm-boc", chatId, "zen", chatId, ct, ["climax-default"]);
        initialRoom.Participants.Select(p => p.AgentId).Should().NotContain("planthy-pm");

        // User asks a Project Management / WBS / timeline question WITHOUT @mention
        var decision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc",
            chatId,
            chatId,
            "Lập bảng kế hoạch tiến độ timeline, lộ trình milestone, phân công WBS và checklist nghiệm thu theo từng tuần");

        decision.TriageOwnerId.Should().Be("agentteams-manager");
        decision.ShouldUseRoom.Should().BeTrue();
        decision.Mode.Should().Be("manager_specialist_recruitment");
        decision.AutoRecruitedCollaborators.Should().Equal(["planthy-pm"]);
        decision.SelectedCollaborators.Should().Equal(["planthy-pm"]);
        decision.AssignedAgents.Should().Equal(["planthy-pm", "gm-boc"]);
        decision.Reason.Should().Contain("mời bổ sung @planthy-pm vào Room");

        // Executing the wave adds planthy-pm into the EXACT SAME single Room
        var (updatedRoom, _) = await orchestrator.ExecuteCollaboratorWaveAsync(
            "gm-boc",
            chatId,
            decision.SelectedCollaborators,
            "Lập bảng kế hoạch tiến độ timeline, lộ trình milestone, phân công WBS và checklist nghiệm thu theo từng tuần",
            null,
            chatId,
            decision.CollaboratorFocuses,
            decision.ExecutionStrategy,
            null,
            ct);

        roomStore.ListByHostAgent("gm-boc").Should().HaveCount(1);
        updatedRoom.RoomId.Should().Be(initialRoom.RoomId);
        updatedRoom.Participants.Select(p => p.AgentId).Should().Contain("planthy-pm");
    }

    [Fact]
    public async Task ManagerAdaptiveSpecialtyLearning_LearnsNovelProjectKeywordsFromQualityGate_AndRoutesSubsequentTurns()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient
        {
            NextReply = "- **Phân tích dòng sản phẩm Sourdough Kombucha:**\n  + Định mức lên men tự nhiên đạt chuẩn."
        };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "session-adaptive-learning-01";
        await orchestrator.EnsureRoomAsync("gm-boc", chatId, "zen", chatId, ct, ["planthy-pm"]);

        // 1. Wave 1: Explicit @zen call on a novel project topic ("sourdough kombucha") not in static keywords
        await orchestrator.ExecuteCollaboratorTurnAsync(
            "gm-boc",
            chatId,
            "zen",
            "@zen đánh giá dòng sourdough kombucha",
            null,
            chatId,
            ct);
        await orchestrator.RecordHostSynthesisTurnAsync(
            "gm-boc",
            chatId,
            "Đã tổng hợp đánh giá sourdough kombucha.",
            null,
            null,
            chatId,
            ct);

        // 2. Verify AdaptiveSpecialtyProfile learned "sourdough" and "kombucha" from the approved manager_quality_gate audit!
        var zenProfile = roomStore.GetAdaptiveSpecialtyProfile("zen", "gm-boc");
        zenProfile.AuditedTurnsCount.Should().Be(1);
        zenProfile.ApprovalRate.Should().Be(1.0);
        zenProfile.ReliabilityBonus.Should().Be(2);
        zenProfile.LearnedKeywordWeights.Should().ContainKey("sourdough");
        zenProfile.LearnedKeywordWeights.Should().ContainKey("kombucha");

        // 3. Wave 2: User asks about "sourdough kombucha" WITHOUT @mention and WITHOUT static sales keywords
        // -> AgentTeams Manager uses the learned keyword weights to route directly to @zen!
        var adaptiveDecision = orchestrator.DecideRoomCollaboratorsForTurn(
            "gm-boc",
            chatId,
            chatId,
            "Kiểm tra lại thông số sourdough kombucha giúp anh nhé");

        adaptiveDecision.TriageOwnerId.Should().Be("agentteams-manager");
        adaptiveDecision.ShouldUseRoom.Should().BeTrue();
        adaptiveDecision.SelectedCollaborators.Should().Equal(["zen"]);
        adaptiveDecision.AdaptiveScores.Should().NotBeNull();
        adaptiveDecision.AdaptiveScores!["zen"].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AdaptiveSpecialtyTimeDecay_AgesOutStaleCampaignKeywords_WhilePreservingFreshCampaignKeywords()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var now = DateTimeOffset.UtcNow;
        var staleTime = now.AddDays(-45); // > 3 half-lives (45 days old -> decay factor ~0.108, 2.0 * 0.108 = 0.21 < 0.5 -> rounds to 0)

        var auditPass = new ManagerOutputAuditRecord(
            Passed: true,
            QualityScore: 0.95,
            Verdict: "approved",
            ChecklistPassed: ["substantive_output", "clean_identity_boundary"],
            IssuesDetected: [],
            ManagerDirective: "Approved",
            AutoRetried: false);

        var room = new SessionCollaborationRoom(
            RoomId: "room_gm-boc_decay01",
            MatrixRoomId: "!decay01:agentteams-gateway.hmz.one",
            ElementRoomUrl: "https://chat.hmz.one/#/room/!decay01",
            HostAgentId: "gm-boc",
            HostChatId: "chat-decay-01",
            HostSessionId: "chat-decay-01",
            Participants:
            [
                new SessionRoomParticipant("gm-boc", "GM Bốc", "host", "@gm-default:agentteams-gateway.hmz.one", "idle", staleTime),
                new SessionRoomParticipant("climax-default", "Climax Default", "collaborator", "@climax-default:agentteams-gateway.hmz.one", "completed", staleTime)
            ],
            Status: "completed",
            ActiveCollaboratorId: null,
            MaxRoomTurns: 4,
            CurrentTurnCount: 4,
            Turns:
            [
                new SessionRoomTurn("rt_old_req", 1, "gm-boc", "GM Bốc", "host", "request", "@climax-default thiết kế hộp bánh mooncake cổ truyền", "climax-default", staleTime),
                new SessionRoomTurn("rt_old_rep", 2, "climax-default", "Climax Default", "collaborator", "collaborator_reply", "Phương án mooncake cổ truyền.", "gm-boc", staleTime, ManagerAudit: auditPass),
                new SessionRoomTurn("rt_new_req", 3, "gm-boc", "GM Bốc", "host", "request", "@climax-default thiết kế bao bì sourdough artisanal", "climax-default", now),
                new SessionRoomTurn("rt_new_rep", 4, "climax-default", "Climax Default", "collaborator", "collaborator_reply", "Phương án sourdough artisanal.", "gm-boc", now, ManagerAudit: auditPass)
            ],
            CreatedAt: staleTime,
            UpdatedAt: now);

        await roomStore.SaveAsync(room);

        var profile = roomStore.GetAdaptiveSpecialtyProfile("climax-default", "gm-boc", now, halfLifeDays: 14.0);
        profile.AuditedTurnsCount.Should().Be(2);
        profile.DecayHalfLifeDays.Should().Be(14.0);
        profile.AgedOutKeywordsCount.Should().BeGreaterThan(0);

        // Stale 45-day-old campaign keyword ("mooncake") has aged out, while fresh keyword ("sourdough" / "artisanal") is active!
        profile.LearnedKeywordWeights.Should().NotContainKey("mooncake");
        profile.LearnedKeywordWeights.Should().ContainKey("sourdough");
        profile.LearnedKeywordWeights["sourdough"].Should().Be(2);
    }

    [Fact]
    public async Task SmartSoloToMultiAgentTransition_PreservesSoloMemoryAndSuggestsSpecialistsSmoothly()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient
        {
            NextReply = "- **[Đề xuất từ chuyên viên]**: Khung báo giá B2B và truyền thông dựa trên ngữ cảnh phiên solo trước."
        };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "chat-solo-to-multi-01";
        var soloQuery = "Anh đang chuẩn bị ra mắt dòng bánh mì chay sourdough tại TP.HCM, cần tính toán bảng báo giá đại lý B2B và chiến dịch truyền thông thương hiệu TikTok";

        // 1. In Solo Mode (before any collaborators join the Room), Manager keeps ShouldUseRoom=false (0ms solo latency)
        // while detecting cross-domain specialists (zen, climax-default) in SuggestedCollaborators!
        var soloDecision = orchestrator.DecideRoomCollaboratorsForTurn("gm-boc", chatId, chatId, soloQuery);
        soloDecision.ShouldUseRoom.Should().BeFalse();
        soloDecision.Mode.Should().Be("no_room_collaborators");
        soloDecision.SuggestedCollaborators.Should().NotBeNull();
        soloDecision.SuggestedCollaborators.Should().Contain(["zen", "climax-default"]);

        var soloContext = orchestrator.BuildSoloSmartContextInjection("gm-boc", chatId, chatId, soloQuery, soloDecision);
        soloContext.Should().NotBeNullOrWhiteSpace();
        soloContext.Should().Contain("[SHARED SESSION ROOM CONTEXT]");
        soloContext.Should().Contain("Smart Solo Mode");

        // Record Turn 1 (Solo Request + Solo Host Synthesis) in the session's Room
        await orchestrator.EnsureRoomAsync("gm-boc", chatId, null, chatId, ct);
        await orchestrator.RecordManagerTriageTurnAsync("gm-boc", chatId, chatId, soloQuery, soloDecision, ct);
        await orchestrator.RecordHostSynthesisTurnAsync("gm-boc", chatId, "Dưới góc độ điều hành, anh nên chia 2 giai đoạn B2B và truyền thông.", "Phân tích solo đa chiều", null, chatId, ct);

        // 2. Smooth Switch to Multi-Agent in the SAME session: User now pulls in @zen and @climax-default
        var multiQuery = "@zen @climax-default triển khai chi tiết phương án vừa thống nhất ở trên nhé";
        var multiDecision = orchestrator.DecideRoomCollaboratorsForTurn("gm-boc", chatId, chatId, multiQuery);
        multiDecision.ShouldUseRoom.Should().BeTrue();
        multiDecision.SelectedCollaborators.Should().BeEquivalentTo(["zen", "climax-default"]);

        var (waveRoom, waveTurns) = await orchestrator.ExecuteCollaboratorWaveAsync(
            "gm-boc", chatId, multiDecision.SelectedCollaborators, multiQuery, null, chatId, multiDecision.CollaboratorFocuses, "sequential", null, ct);

        waveTurns.Should().HaveCount(2);
        // Collaborators received the prior solo request & synthesis from Turn 1 in their scoped prompt!
        a2a.LastMessageSent.Should().Contain("Dưới góc độ điều hành, anh nên chia 2 giai đoạn B2B và truyền thông.");

        // 3. Smooth Switch back to Host Solo in the SAME session: Host retains verified collaborator insights
        var backToSoloQuery = "Chỉ mình bạn trả lời thôi: chọn giúp anh 1 ưu tiên số 1 trong tuần đầu tiên từ ý kiến của phòng";
        var backToSoloDecision = orchestrator.DecideRoomCollaboratorsForTurn("gm-boc", chatId, chatId, backToSoloQuery);
        backToSoloDecision.ShouldUseRoom.Should().BeFalse();
        backToSoloDecision.Mode.Should().Be("host_solo");

        var continuityContext = orchestrator.BuildSoloSmartContextInjection("gm-boc", chatId, chatId, backToSoloQuery, backToSoloDecision);
        continuityContext.Should().NotBeNullOrWhiteSpace();
        continuityContext.Should().Contain("Khung báo giá B2B và truyền thông dựa trên ngữ cảnh phiên solo trước");
    }

    [Fact]
    public async Task ManagerHostRepresentativeGovernance_DecidesHostSelfExecution_AndAlwaysGuaranteesHostAsFinalRoomSynthesizer()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient();
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "chat-host-rep-governance-01";
        await orchestrator.EnsureRoomAsync("mons", chatId, "zen", chatId, ct, ["ken"]);

        // Case 1: Request in @mons session only covers Sales (@zen) + Product (@ken), NOT @mons Dev/Infra specialty.
        // Manager decides: @mons does NOT need to self-execute ("synthesize_only"), but is GUARANTEED as SynthesisOwnerId ("mons") to synthesize after Room is done!
        var synthOnlyQuery = "@zen @ken lên bảng báo giá đại lý B2B và danh mục user story nghiệm thu sản phẩm";
        var synthOnlyDecision = orchestrator.DecideRoomCollaboratorsForTurn("mons", chatId, chatId, synthOnlyQuery);
        synthOnlyDecision.ShouldUseRoom.Should().BeTrue();
        synthOnlyDecision.SynthesisOwnerId.Should().Be("mons");
        synthOnlyDecision.HostShouldSelfExecute.Should().BeFalse();
        synthOnlyDecision.HostExecutionRole.Should().Be("synthesize_only");

        var room = roomStore.GetByChat("mons", chatId)!;
        var synthOnlyBrief = orchestrator.BuildManagerSynthesisBrief(room, synthOnlyQuery, synthOnlyDecision, []);
        synthOnlyBrief.Should().Contain("[KHÔNG CẦN TỰ CHẠY LẶP LẠI]");
        synthOnlyBrief.Should().Contain("[ĐẠI DIỆN ROOM TỔNG HỢP CUỐI]");

        // Case 2: Request in @mons session covers Sales (@zen) AND Backend/Docker Architecture (@mons specialty).
        // Manager decides: @mons BOTH self-executes its own specialty ("co_execute_and_synthesize") AND acts as the Room Representative synthesizer ("mons")!
        var coExecQuery = "@zen báo giá gói B2B và thiết kế kiến trúc backend API database docker deploy trên server";
        var coExecDecision = orchestrator.DecideRoomCollaboratorsForTurn("mons", chatId, chatId, coExecQuery);
        coExecDecision.ShouldUseRoom.Should().BeTrue();
        coExecDecision.SynthesisOwnerId.Should().Be("mons");
        coExecDecision.HostShouldSelfExecute.Should().BeTrue();
        coExecDecision.HostExecutionRole.Should().Be("co_execute_and_synthesize");

        var coExecBrief = orchestrator.BuildManagerSynthesisBrief(room, coExecQuery, coExecDecision, []);
        coExecBrief.Should().Contain("[TỰ THỰC THI PHẦN VIỆC CỦA CHỦ SESSION]");
        coExecBrief.Should().Contain("[ĐẠI DIỆN ROOM TỔNG HỢP CUỐI]");
    }

    [Fact]
    public async Task HostSynthesisCoverageAudit_And_HostExecutionPolicyOverride_WorkAsExpected()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomStore = new SessionRoomStore();
        var subStore = CreateTempSubAgentStore();
        var a2a = new FakeRoomA2AClient
        {
            NextReply = "- **[Đóng góp chuyên môn]**: Phân tích chi tiết đạt chuẩn."
        };
        var queue = new SessionQueueManager();
        var orchestrator = new SessionRoomOrchestrator(roomStore, subStore, a2a, queue, NullLogger<SessionRoomOrchestrator>.Instance);

        var chatId = "chat-synth-audit-policy-01";
        await orchestrator.EnsureRoomAsync("mons", chatId, "zen", chatId, ct, ["ken"]);

        // 1. Test Proposal 5: HostExecutionPolicy = "co_execute" forces HostShouldSelfExecute = true even on a pure sales query
        await orchestrator.SetRoomTriagePolicyAsync("mons", chatId, "auto", ct, "co_execute");
        var forcedCoExec = orchestrator.DecideRoomCollaboratorsForTurn("mons", chatId, chatId, "@zen @ken lên bảng báo giá bán B2B");
        forcedCoExec.HostShouldSelfExecute.Should().BeTrue();
        forcedCoExec.HostExecutionRole.Should().Be("co_execute_and_synthesize");

        // 2. Test Proposal 5: HostExecutionPolicy = "synthesize_only" forces HostShouldSelfExecute = false even on a backend/docker query
        await orchestrator.SetRoomTriagePolicyAsync("mons", chatId, "auto", ct, "synthesize_only");
        var forcedSynthOnly = orchestrator.DecideRoomCollaboratorsForTurn("mons", chatId, chatId, "@zen báo giá và triển khai backend API docker");
        forcedSynthOnly.HostShouldSelfExecute.Should().BeFalse();
        forcedSynthOnly.HostExecutionRole.Should().Be("synthesize_only");

        // 3. Test Proposal 2: Execute wave for zen & ken, then RecordHostSynthesisTurnAsync attaches ManagerSynthesisCoverageAudit (100%_room_coverage)
        await orchestrator.ExecuteCollaboratorWaveAsync("mons", chatId, ["zen", "ken"], "@zen @ken phân tích gói B2B", null, chatId, null, "parallel", null, ct);
        var synthRes = await orchestrator.RecordHostSynthesisTurnAsync(
            "mons",
            chatId,
            "Tổng hợp đại diện Room từ @zen và @ken: Phương án gói B2B kèm lộ trình sản phẩm hoàn chỉnh.",
            "Hội tụ kết quả từ cả Room",
            null,
            chatId,
            ct);

        synthRes.Should().NotBeNull();
        var synthTurn = synthRes!.Value.Turn;
        synthTurn.ManagerAudit.Should().NotBeNull();
        synthTurn.ManagerAudit!.Passed.Should().BeTrue();
        synthTurn.ManagerAudit.Verdict.Should().Be("synthesis_verified");
        synthTurn.ManagerAudit.ChecklistPassed.Should().Contain(["covered:@zen", "covered:@ken", "100%_room_coverage"]);

        // 4. Verify that when A2A returns empty/fallback, ExecuteCollaboratorTurnAsync invokes BuildSpecialtySynthesisFallback (never emitting literal "BuildSpecialtySynthesisFallback(collab, objective)")
        a2a.NextReply = "";
        var (_, fallbackTurn) = await orchestrator.ExecuteCollaboratorTurnAsync("mons", chatId, "zen", "Tư vấn báo giá B2B", null, chatId, ct);
        fallbackTurn.Text.Should().NotContain("BuildSpecialtySynthesisFallback");
        fallbackTurn.Text.Should().Contain("Chiến lược Thương mại & Cấu trúc Doanh thu");
    }
}
