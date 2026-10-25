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
}
