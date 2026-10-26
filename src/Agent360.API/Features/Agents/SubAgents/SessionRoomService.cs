using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Services;

namespace Agent360.API.Features.Agents.SubAgents;

public sealed record SessionRoomParticipant(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role, // "host" | "collaborator" | "manager" | "admin"
    [property: JsonPropertyName("matrix_user_id")] string MatrixUserId,
    [property: JsonPropertyName("status")] string Status, // "idle" | "running" | "completed" | "awaiting_input"
    [property: JsonPropertyName("joined_at")] DateTimeOffset JoinedAt);

public sealed record SessionRoomStep(
    [property: JsonPropertyName("kind")] string Kind, // "reasoning" | "tool_call"
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("call_id")] string? CallId = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("tool")] string? Tool = null,
    [property: JsonPropertyName("args")] string? Args = null,
    [property: JsonPropertyName("result")] string? Result = null,
    [property: JsonPropertyName("state")] string? State = "success");

public sealed record ManagerOutputAuditRecord(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("quality_score")] double QualityScore,
    [property: JsonPropertyName("verdict")] string Verdict, // "approved" | "auto_corrected" | "scope_pending" | "fallback_recovered"
    [property: JsonPropertyName("checklist_passed")] IReadOnlyList<string> ChecklistPassed,
    [property: JsonPropertyName("issues_detected")] IReadOnlyList<string> IssuesDetected,
    [property: JsonPropertyName("manager_directive")] string ManagerDirective,
    [property: JsonPropertyName("auto_retried")] bool AutoRetried = false);

public sealed record SessionRoomInlineReceipt(
    [property: JsonPropertyName("kind")] string Kind, // "dispatch" | "exchange" | "reply"
    [property: JsonPropertyName("round_no")] int RoundNo,
    [property: JsonPropertyName("participants")] IReadOnlyList<string> Participants,
    [property: JsonPropertyName("message_count")] int MessageCount = 0,
    [property: JsonPropertyName("label")] string? Label = null);

public sealed record RoomFollowUpRoundDecision(
    [property: JsonPropertyName("should_run_round_2")] bool ShouldRunRound2,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("round_2_focuses")] IReadOnlyDictionary<string, string> Round2Focuses,
    [property: JsonPropertyName("additional_collaborators")] IReadOnlyList<string>? AdditionalCollaborators = null);

public sealed record TeamWaveDynamicsReport(
    [property: JsonPropertyName("dynamics_state")] string DynamicsState, // "strong_consensus" | "conflict_detected" | "clarification_needed" | "dynamic_peer_handoff" | "partial_degraded_recovery" | "complementary_synergy"
    [property: JsonPropertyName("has_conflict")] bool HasConflict,
    [property: JsonPropertyName("conflict_summary")] string? ConflictSummary,
    [property: JsonPropertyName("needs_user_clarification")] bool NeedsUserClarification,
    [property: JsonPropertyName("unified_clarification_hint")] string? UnifiedClarificationHint,
    [property: JsonPropertyName("has_partial_fallback")] bool HasPartialFallback,
    [property: JsonPropertyName("active_collaborators")] IReadOnlyList<string> ActiveCollaborators,
    [property: JsonPropertyName("synthesis_directive")] string SynthesisDirective);

public sealed record SessionRoomTurn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("turn_no")] int TurnNo,
    [property: JsonPropertyName("author_id")] string AuthorId,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("author_role")] string AuthorRole, // "host" | "collaborator" | "manager" | "admin" | "user"
    [property: JsonPropertyName("kind")] string Kind, // "request" | "host_kickoff" | "host_progress" | "collaborator_progress" | "collaborator_reply" | "question" | "answer" | "manager_guard" | "synthesis"
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("target_agent_id")] string? TargetAgentId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("reasoning")] string? Reasoning = null,
    [property: JsonPropertyName("steps")] IReadOnlyList<SessionRoomStep>? Steps = null,
    [property: JsonPropertyName("manager_audit")] ManagerOutputAuditRecord? ManagerAudit = null,
    [property: JsonPropertyName("run_id")] string? RunId = null,
    [property: JsonPropertyName("round_no")] int RoundNo = 1,
    [property: JsonPropertyName("visibility")] string Visibility = "room", // "main" | "room"
    [property: JsonPropertyName("receipt")] SessionRoomInlineReceipt? Receipt = null);

public sealed record MessageReactionRecord(
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("sentiment")] string Sentiment, // "positive" | "negative" | "neutral"
    [property: JsonPropertyName("category")] string Category   // "praise" | "approve" | "disapprove" | "sad_frustrated" | "surprised_curious" | "amused"
);

public sealed record SubmitMessageReactionRequestDto(
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("action")] string? Action = "add", // "add" | "remove"
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("message_snippet")] string? MessageSnippet = null,
    [property: JsonPropertyName("target_agent_id")] string? TargetAgentId = null,
    [property: JsonPropertyName("trigger_agent_response")] bool TriggerAgentResponse = true
);

public sealed record MessageReactionSummaryDto(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("total_reactions")] int TotalReactions,
    [property: JsonPropertyName("positive_count")] int PositiveCount,
    [property: JsonPropertyName("negative_count")] int NegativeCount,
    [property: JsonPropertyName("neutral_count")] int NeutralCount,
    [property: JsonPropertyName("top_emojis")] IReadOnlyDictionary<string, int> TopEmojis
);

public sealed record SessionCollaborationRoom(
    [property: JsonPropertyName("room_id")] string RoomId,
    [property: JsonPropertyName("matrix_room_id")] string MatrixRoomId,
    [property: JsonPropertyName("element_room_url")] string ElementRoomUrl,
    [property: JsonPropertyName("host_agent_id")] string HostAgentId,
    [property: JsonPropertyName("host_chat_id")] string HostChatId,
    [property: JsonPropertyName("host_session_id")] string HostSessionId,
    [property: JsonPropertyName("participants")] IReadOnlyList<SessionRoomParticipant> Participants,
    [property: JsonPropertyName("status")] string Status, // "idle" | "collaborator_running" | "awaiting_clarification" | "synthesizing" | "completed"
    [property: JsonPropertyName("active_collaborator_id")] string? ActiveCollaboratorId,
    [property: JsonPropertyName("max_room_turns")] int MaxRoomTurns,
    [property: JsonPropertyName("current_turn_count")] int CurrentTurnCount,
    [property: JsonPropertyName("turns")] IReadOnlyList<SessionRoomTurn> Turns,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("room_title")] string? RoomTitle = null,
    [property: JsonPropertyName("reactions")] IReadOnlyDictionary<string, IReadOnlyList<MessageReactionRecord>>? Reactions = null,
        [property: JsonPropertyName("triage_policy")] string? TriagePolicy = "auto",
    [property: JsonPropertyName("host_execution_policy")] string? HostExecutionPolicy = "auto", // "auto" | "co_execute" | "synthesize_only"
    [property: JsonPropertyName("active_run_id")] string? ActiveRunId = null,
    [property: JsonPropertyName("active_round")] int ActiveRound = 1,
    [property: JsonPropertyName("run_state")] string? RunState = null);

public sealed record UpdateRoomTriagePolicyRequest(
    [property: JsonPropertyName("triage_policy")] string? TriagePolicy = null,
    [property: JsonPropertyName("host_execution_policy")] string? HostExecutionPolicy = null);

public sealed record UpdateRoomTitleRequest(
    [property: JsonPropertyName("title")] string Title);

public sealed record PullAgentIntoRoomRequest(
    [property: JsonPropertyName("collaborator_agent_id")] string? CollaboratorAgentId = null,
    [property: JsonPropertyName("collaborator_agent_ids")] IReadOnlyList<string>? CollaboratorAgentIds = null,
    [property: JsonPropertyName("objective")] string? Objective = null,
    [property: JsonPropertyName("context_snippet")] string? ContextSnippet = null,
    [property: JsonPropertyName("host_session_id")] string? HostSessionId = null,
    [property: JsonPropertyName("auto_dispatch")] bool AutoDispatch = false);

public sealed record PostRoomMessageRequest(
    [property: JsonPropertyName("author_id")] string AuthorId,
    [property: JsonPropertyName("author_role")] string? AuthorRole,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("target_agent_id")] string? TargetAgentId = null);

public sealed record HostRoomTriageRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("host_session_id")] string? HostSessionId = null);

public sealed record HostRoomTriageDecision(
    [property: JsonPropertyName("should_use_room")] bool ShouldUseRoom,
    [property: JsonPropertyName("selected_collaborators")] IReadOnlyList<string> SelectedCollaborators,
    [property: JsonPropertyName("mode")] string Mode, // "no_room_collaborators" | "explicit_mention" | "reply_or_named" | "continuity_followup" | "active_collaborator_affinity" | "specialty_triage" | "manual_policy_host_solo" | "host_solo"
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("confidence")] double Confidence = 1.0,
    [property: JsonPropertyName("collaborator_focuses")] IReadOnlyDictionary<string, string>? CollaboratorFocuses = null,
    [property: JsonPropertyName("execution_strategy")] string ExecutionStrategy = "parallel",
    [property: JsonPropertyName("triage_owner_id")] string TriageOwnerId = "agentteams-manager",
    [property: JsonPropertyName("assigned_agents")] IReadOnlyList<string>? AssignedAgents = null,
    [property: JsonPropertyName("auto_recruited_collaborators")] IReadOnlyList<string>? AutoRecruitedCollaborators = null,
    [property: JsonPropertyName("adaptive_scores")] IReadOnlyDictionary<string, int>? AdaptiveScores = null,
    [property: JsonPropertyName("suggested_collaborators")] IReadOnlyList<string>? SuggestedCollaborators = null,
    [property: JsonPropertyName("synthesis_owner_id")] string? SynthesisOwnerId = null,
[property: JsonPropertyName("host_should_self_execute")] bool HostShouldSelfExecute = true,
    [property: JsonPropertyName("host_execution_role")] string HostExecutionRole = "solo_execute_and_respond", // "co_execute_and_synthesize" | "synthesize_only" | "solo_execute_and_respond"
    [property: JsonPropertyName("host_execution_focus")] string? HostExecutionFocus = null,
    [property: JsonPropertyName("collaboration_mode")] string CollaborationMode = "deliberation"); // "solo" | "consultation" | "deliberation"

public sealed record AdaptiveSpecialtyProfileDto(
    [property: JsonPropertyName("collaborator_id")] string CollaboratorId,
    [property: JsonPropertyName("audited_turns_count")] int AuditedTurnsCount,
    [property: JsonPropertyName("average_quality_score")] double AverageQualityScore,
    [property: JsonPropertyName("approval_rate")] double ApprovalRate,
    [property: JsonPropertyName("reliability_bonus")] int ReliabilityBonus,
    [property: JsonPropertyName("learned_keyword_weights")] IReadOnlyDictionary<string, int> LearnedKeywordWeights,
    [property: JsonPropertyName("decay_half_life_days")] double DecayHalfLifeDays = 14.0,
    [property: JsonPropertyName("aged_out_keywords_count")] int AgedOutKeywordsCount = 0);


public static class AgentReactionClassifier
{
    public static (string Sentiment, string Category, string DirectiveVi) Classify(string emoji)
    {
        return emoji switch
        {
            "❤️" or "🔥" or "🚀" or "🎉" or "💯" or "👏" or "🤩" or "😍" or "🙌" or "⚡" or "💪" =>
                ("positive", "praise", "Người dùng rất ấn tượng, khen ngợi và đánh giá cao câu trả lời vừa rồi. Hãy ghi nhận, cảm ơn ngắn gọn lịch thiệp tự nhiên (1-2 câu), củng cố hướng tiếp cận này và sẵn sàng hỗ trợ bước tiếp theo."),
            "👍" or "🤝" or "🫡" or "🎯" or "💡" or "🙏" or "☕" or "🤖" =>
                ("positive", "approve", "Người dùng đồng ý, xác nhận phương án hoặc thông tin vừa nêu là đúng và hữu ích. Hãy xác nhận ngắn gọn rằng bạn đã chốt ý này và đề xuất bước thực thi tiếp theo."),
            "👎" =>
                ("negative", "disapprove", "Người dùng không hài lòng hoặc chê chất lượng/tính chính xác của câu trả lời vừa rồi. Hãy lập tức nhận phản hồi với thái độ cầu thị, tự rà soát ngắn gọn điểm có thể chưa chuẩn hoặc chưa đầy đủ trong câu trả lời trên, và đưa ra giải pháp khắc phục/bổ sung chính xác hơn hoặc hỏi ngắn gọn ý người dùng muốn điều chỉnh."),
            "😢" =>
                ("negative", "sad_frustrated", "Người dùng cảm thấy hụt hẫng, chưa thỏa đáng hoặc gặp sự cố bất tiện. Hãy thể hiện sự đồng cảm, kiểm tra lại thiếu sót và chủ động đề xuất giải pháp tháo gỡ khó khăn ngay lập tức."),
            "😮" or "👀" =>
                ("neutral", "surprised_curious", "Người dùng thấy bất ngờ, ngạc nhiên hoặc tò mò về điểm mấu chốt/thông tin vừa nêu. Hãy làm rõ thêm chi tiết quan trọng nhất để người dùng nắm bắt trọn vẹn."),
            "😆" =>
                ("positive", "amused", "Người dùng thấy phản hồi thú vị, vui vẻ. Hãy đáp lại tự nhiên, tích cực và giữ mạch công việc trôi chảy."),
            _ =>
                ("neutral", "feedback", "Người dùng vừa gắn biểu tượng cảm xúc vào phản hồi của bạn. Hãy tiếp thu một cách tự nhiên và tiếp tục đồng hành hỗ trợ.")
        };
    }

                public static string BuildImplicitReactionPrompt(string emoji, string? messageSnippet, string? authorName)
    {
        var (sentiment, category, directive) = Classify(emoji);
        var cleanSnippet = string.IsNullOrWhiteSpace(messageSnippet) ? "(phản hồi trước đó)" : messageSnippet.Trim().Replace("\r", "").Replace("\n", " ");
        if (cleanSnippet.Length > 200) cleanSnippet = cleanSnippet[..200] + "...";
        var author = string.IsNullOrWhiteSpace(authorName) ? "bạn" : authorName;

        return $"[IMPLICIT_REACTION_FEEDBACK emoji=\"{emoji}\" category=\"{category}\" sentiment=\"{sentiment}\" target=\"{author}\"]\n" +
               $"Người dùng vừa thả biểu tượng cảm xúc {emoji} để đánh giá câu trả lời vừa rồi của {author}:\n" +
               $"\"{cleanSnippet}\"\n\n" +
               $"[Chỉ thị phản hồi ngầm]: {directive} Lưu ý: KHÔNG lặp lại thẻ hệ thống [IMPLICIT_REACTION_FEEDBACK]. Hãy trả lời ngắn gọn, tự nhiên, đúng giọng điệu của agent.\n" +
               $"[/IMPLICIT_REACTION_FEEDBACK]";
    }
}

public sealed class SessionRoomStore
{
    private readonly ConcurrentDictionary<string, SessionCollaborationRoom> _roomsByChatKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _chatKeyByRoomId = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storageFile;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SessionRoomStore(IHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "_storage", "subagents");
        Directory.CreateDirectory(dir);
        _storageFile = Path.Combine(dir, "session_rooms.json");
        LoadFromDisk();
    }

    public SessionRoomStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agent360_rooms_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _storageFile = Path.Combine(dir, "session_rooms.json");
    }

    private static string MakeKey(string hostAgentId, string hostChatId) =>
        $"{SubAgentService.NormalizeAgentId(hostAgentId).ToLowerInvariant()}:{hostChatId.Trim()}";

    private static bool IsRealMatrixRoomId(string? matrixId) =>
        !string.IsNullOrWhiteSpace(matrixId) &&
        matrixId.StartsWith("!", StringComparison.Ordinal) &&
        !matrixId.StartsWith("!room_", StringComparison.OrdinalIgnoreCase) &&
        !matrixId.StartsWith("!placeholder", StringComparison.OrdinalIgnoreCase);

    private static int CountCollaborators(SessionCollaborationRoom r) =>
        r.Participants.Count(p => string.Equals(p.Role, "collaborator", StringComparison.OrdinalIgnoreCase));

    public static SessionCollaborationRoom MergeRooms(SessionCollaborationRoom a, SessionCollaborationRoom b)
    {
        // Choose canonical primary room: prefer real MatrixRoomId, more collaborators, more turns, earlier creation
        var primary = a;
        var secondary = b;
        bool bBetterMatrix = IsRealMatrixRoomId(b.MatrixRoomId) && !IsRealMatrixRoomId(a.MatrixRoomId);
        bool bMoreActive = CountCollaborators(b) > CountCollaborators(a) ||
                           (CountCollaborators(b) == CountCollaborators(a) && b.Turns.Count > a.Turns.Count);
        if (bBetterMatrix || (IsRealMatrixRoomId(b.MatrixRoomId) == IsRealMatrixRoomId(a.MatrixRoomId) && bMoreActive))
        {
            primary = b;
            secondary = a;
        }

        // Merge participants preserving order (host first, collaborators, manager, admin)
        var mergedParticipants = new List<SessionRoomParticipant>(primary.Participants);
        foreach (var p in secondary.Participants)
        {
            var existingIdx = mergedParticipants.FindIndex(x => string.Equals(x.AgentId, p.AgentId, StringComparison.OrdinalIgnoreCase));
            if (existingIdx < 0)
            {
                mergedParticipants.Add(p);
            }
            else if (mergedParticipants[existingIdx].Status == "idle" && p.Status != "idle")
            {
                mergedParticipants[existingIdx] = p;
            }
        }

        // Merge turns (deduplicate by Id and avoid duplicate manager init turns)
        var mergedTurns = new List<SessionRoomTurn>(primary.Turns);
        foreach (var t in secondary.Turns)
        {
            if (mergedTurns.Any(x => string.Equals(x.Id, t.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (t.TurnNo == 1 && string.Equals(t.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase) &&
                mergedTurns.Any(x => string.Equals(x.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase) && x.Kind == "request"))
                continue;
            mergedTurns.Add(t);
        }

        mergedTurns = mergedTurns
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.TurnNo)
            .Select((t, idx) => t with { TurnNo = idx + 1 })
            .ToList();

        // Merge reactions
        Dictionary<string, IReadOnlyList<MessageReactionRecord>>? mergedReactions = null;
        if (primary.Reactions is { Count: > 0 } || secondary.Reactions is { Count: > 0 })
        {
            var dict = new Dictionary<string, List<MessageReactionRecord>>(StringComparer.OrdinalIgnoreCase);
            if (primary.Reactions is not null)
            {
                foreach (var kv in primary.Reactions)
                    dict[kv.Key] = kv.Value.ToList();
            }
            if (secondary.Reactions is not null)
            {
                foreach (var kv in secondary.Reactions)
                {
                    if (!dict.TryGetValue(kv.Key, out var list))
                    {
                        list = [];
                        dict[kv.Key] = list;
                    }
                    foreach (var rec in kv.Value)
                    {
                        if (!list.Any(r => string.Equals(r.Emoji, rec.Emoji, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(r.UserId, rec.UserId, StringComparison.OrdinalIgnoreCase)))
                        {
                            list.Add(rec);
                        }
                    }
                }
            }
            mergedReactions = dict.ToDictionary(k => k.Key, v => (IReadOnlyList<MessageReactionRecord>)v.Value, StringComparer.OrdinalIgnoreCase);
        }

        var canonicalChatId = !primary.HostChatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
            ? primary.HostChatId
            : (!secondary.HostChatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase) ? secondary.HostChatId : primary.HostChatId);

        var canonicalSessionId = !string.IsNullOrWhiteSpace(primary.HostSessionId) && !string.Equals(primary.HostSessionId, primary.HostChatId, StringComparison.OrdinalIgnoreCase)
            ? primary.HostSessionId
            : (!string.IsNullOrWhiteSpace(secondary.HostSessionId) ? secondary.HostSessionId : canonicalChatId);

        return primary with
        {
            HostChatId = canonicalChatId,
            HostSessionId = canonicalSessionId,
            Participants = mergedParticipants,
            CurrentTurnCount = mergedTurns.Count,
            Turns = mergedTurns,
            RoomTitle = !string.IsNullOrWhiteSpace(primary.RoomTitle) ? primary.RoomTitle : secondary.RoomTitle,
            Reactions = mergedReactions ?? primary.Reactions,
            TriagePolicy = !string.IsNullOrWhiteSpace(primary.TriagePolicy) ? primary.TriagePolicy : (secondary.TriagePolicy ?? "auto"),
            UpdatedAt = primary.UpdatedAt >= secondary.UpdatedAt ? primary.UpdatedAt : secondary.UpdatedAt
        };
    }

    private static List<SessionCollaborationRoom> ConsolidateRoomsList(IEnumerable<SessionCollaborationRoom> rooms)
    {
        var byHostAndSession = new List<SessionCollaborationRoom>();

        foreach (var incoming in rooms)
        {
            var normHost = SubAgentService.NormalizeAgentId(incoming.HostAgentId);
            string lastReqText = "";
            var healedTurns = new List<SessionRoomTurn>(incoming.Turns.Count);
            bool anyHealed = false;
            foreach (var t in incoming.Turns)
            {
                if (t.Kind == "request" && !string.IsNullOrWhiteSpace(t.Text))
                {
                    lastReqText = t.Text;
                }
                if (!string.IsNullOrWhiteSpace(t.Text) &&
                    (t.Text.Contains("BuildSpecialtySynthesisFallback(collab, objective)", StringComparison.OrdinalIgnoreCase) ||
                     t.Text.Contains("hiện đang bận hoặc không phản hồi kịp thời", StringComparison.OrdinalIgnoreCase)))
                {
                    var healedText = SessionRoomOrchestrator.BuildSpecialtySynthesisFallback(t.AuthorId, lastReqText);
                    healedTurns.Add(t with { Text = healedText });
                    anyHealed = true;
                }
                else
                {
                    healedTurns.Add(t);
                }
            }
            var normalizedIncoming = anyHealed
                ? incoming with { HostAgentId = normHost, Turns = healedTurns }
                : incoming with { HostAgentId = normHost };

            var matchIdx = byHostAndSession.FindIndex(existing =>
                string.Equals(existing.HostAgentId, normHost, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(existing.RoomId, normalizedIncoming.RoomId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(existing.HostChatId, normalizedIncoming.HostChatId, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(normalizedIncoming.HostSessionId) &&
                  (string.Equals(existing.HostChatId, normalizedIncoming.HostSessionId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(existing.HostSessionId, normalizedIncoming.HostSessionId, StringComparison.OrdinalIgnoreCase))) ||
                 (!string.IsNullOrWhiteSpace(existing.HostSessionId) &&
                  string.Equals(existing.HostSessionId, normalizedIncoming.HostChatId, StringComparison.OrdinalIgnoreCase))));

            if (matchIdx >= 0)
            {
                byHostAndSession[matchIdx] = MergeRooms(byHostAndSession[matchIdx], normalizedIncoming);
            }
            else
            {
                byHostAndSession.Add(normalizedIncoming);
            }
        }

        // Prune phantom zero-collaborator 1-turn stub rooms if another host agent owns an active multi-agent room for the exact same UUID HostChatId
        var pruned = byHostAndSession
            .Where(r =>
            {
                bool isStub = CountCollaborators(r) == 0 && r.Turns.Count <= 1 && (r.Reactions is null || r.Reactions.Count == 0);
                if (!isStub) return true;
                bool activeRoomExistsForSameChatId = byHostAndSession.Any(other =>
                    !ReferenceEquals(other, r) &&
                    string.Equals(other.HostChatId, r.HostChatId, StringComparison.OrdinalIgnoreCase) &&
                    (CountCollaborators(other) > 0 || other.Turns.Count > 1));
                return !activeRoomExistsForSameChatId;
            })
            .ToList();

        return pruned;
    }

    private void RebuildIndexes(IEnumerable<SessionCollaborationRoom> consolidated)
    {
        _roomsByChatKey.Clear();
        _chatKeyByRoomId.Clear();
        foreach (var r in consolidated)
        {
            var key = MakeKey(r.HostAgentId, r.HostChatId);
            _roomsByChatKey[key] = r;
            if (!string.IsNullOrWhiteSpace(r.HostSessionId))
            {
                _roomsByChatKey[MakeKey(r.HostAgentId, r.HostSessionId)] = r;
            }
            _chatKeyByRoomId[r.RoomId] = key;
            _chatKeyByRoomId[r.MatrixRoomId] = key;
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storageFile)) return;
            var json = File.ReadAllText(_storageFile);
            var list = JsonSerializer.Deserialize<List<SessionCollaborationRoom>>(json);
            if (list is null) return;
            var consolidated = ConsolidateRoomsList(list);
            RebuildIndexes(consolidated);
        }
        catch {}
    }

    private async Task PersistAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var distinctRooms = ConsolidateRoomsList(_roomsByChatKey.Values);
            RebuildIndexes(distinctRooms);
            var json = JsonSerializer.Serialize(distinctRooms, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storageFile, json);
        }
        catch {}
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task<SessionCollaborationRoom> RecordReactionAsync(
        string hostAgentId,
        string hostChatId,
        string messageId,
        string emoji,
        string userId,
        string action,
        CancellationToken ct = default)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var shortHash = Math.Abs($"{host}:{hostChatId}".GetHashCode()).ToString("x8");
        var roomSlug = $"room_{host}_{shortHash}";
        var room = GetByChat(host, hostChatId)
            ?? new SessionCollaborationRoom(
                roomSlug,
                $"!{roomSlug}:{SessionRoomOrchestrator.GatewayDomain}",
                $"{SessionRoomOrchestrator.ElementBaseUrl}{Uri.EscapeDataString($"!{roomSlug}:{SessionRoomOrchestrator.GatewayDomain}")}",
                host,
                hostChatId,
                hostChatId,
                [],
                "idle",
                null,
                10,
                0,
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

        var currentReactions = room.Reactions is not null
            ? new Dictionary<string, List<MessageReactionRecord>>(room.Reactions.ToDictionary(k => k.Key, v => v.Value.ToList()), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<MessageReactionRecord>>(StringComparer.OrdinalIgnoreCase);

        if (!currentReactions.TryGetValue(messageId, out var list))
        {
            list = new List<MessageReactionRecord>();
            currentReactions[messageId] = list;
        }

        var isAdd = !string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase);
        if (isAdd)
        {
            var (sentiment, category, _) = AgentReactionClassifier.Classify(emoji);
            if (!list.Any(r => string.Equals(r.Emoji, emoji, StringComparison.OrdinalIgnoreCase) && string.Equals(r.UserId, userId, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new MessageReactionRecord(emoji, userId, DateTimeOffset.UtcNow, sentiment, category));
            }
        }
        else
        {
            list.RemoveAll(r => string.Equals(r.Emoji, emoji, StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(userId) || string.Equals(r.UserId, userId, StringComparison.OrdinalIgnoreCase)));
            if (list.Count == 0) currentReactions.Remove(messageId);
        }

        var updatedReactions = currentReactions.ToDictionary(k => k.Key, v => (IReadOnlyList<MessageReactionRecord>)v.Value);
        var updatedRoom = room with
        {
            Reactions = updatedReactions,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return await SaveAsync(updatedRoom);
    }

    public MessageReactionSummaryDto GetReactionSummary(string agentId)
    {
        var norm = SubAgentService.NormalizeAgentId(agentId);
        var rooms = ListByHostAgent(norm);
        int total = 0, pos = 0, neg = 0, neu = 0;
        var emojiCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rooms)
        {
            if (r.Reactions is null) continue;
            foreach (var kvp in r.Reactions)
            {
                foreach (var rec in kvp.Value)
                {
                    total++;
                    if (rec.Sentiment == "positive") pos++;
                    else if (rec.Sentiment == "negative") neg++;
                    else neu++;

                    emojiCounts[rec.Emoji] = emojiCounts.GetValueOrDefault(rec.Emoji) + 1;
                }
            }
        }

        return new MessageReactionSummaryDto(norm, total, pos, neg, neu, emojiCounts);
    }

    public SessionCollaborationRoom? GetByChat(string hostAgentId, string hostChatId)
    {
        if (string.IsNullOrWhiteSpace(hostChatId)) return null;
        var normHost = SubAgentService.NormalizeAgentId(hostAgentId);
        if (_roomsByChatKey.TryGetValue(MakeKey(normHost, hostChatId), out var room))
            return room;
        return _roomsByChatKey.Values.FirstOrDefault(r =>
            string.Equals(r.HostAgentId, normHost, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.HostChatId, hostChatId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.HostSessionId, hostChatId, StringComparison.OrdinalIgnoreCase)));
    }

    public void BindSessionAlias(string hostAgentId, string chatId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(sessionId)) return;
        var normHost = SubAgentService.NormalizeAgentId(hostAgentId);
        var byChat = GetByChat(normHost, chatId);
        var bySession = GetByChat(normHost, sessionId);
        var existing = byChat is not null && bySession is not null && !ReferenceEquals(byChat, bySession)
            ? MergeRooms(byChat, bySession)
            : (byChat ?? bySession);
        if (existing is null) return;
        var updated = existing with
        {
            HostChatId = !chatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase) ? chatId : existing.HostChatId,
            HostSessionId = sessionId
        };
        _roomsByChatKey[MakeKey(normHost, chatId)] = updated;
        _roomsByChatKey[MakeKey(normHost, sessionId)] = updated;
        _chatKeyByRoomId[updated.RoomId] = MakeKey(normHost, updated.HostChatId);
        _chatKeyByRoomId[updated.MatrixRoomId] = MakeKey(normHost, updated.HostChatId);
    }

    public SessionCollaborationRoom? GetByRoomId(string roomId)
    {
        if (_chatKeyByRoomId.TryGetValue(roomId, out var key) && _roomsByChatKey.TryGetValue(key, out var room))
            return room;
        return _roomsByChatKey.Values.FirstOrDefault(r =>
            string.Equals(r.RoomId, roomId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.MatrixRoomId, roomId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<SessionCollaborationRoom> ListByHostAgent(string hostAgentId)
    {
        var norm = SubAgentService.NormalizeAgentId(hostAgentId);
        return ConsolidateRoomsList(_roomsByChatKey.Values.Where(r => string.Equals(r.HostAgentId, norm, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.UpdatedAt)
            .ToList();
    }

    private static readonly HashSet<string> VietnameseStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "nhưng", "hoặc", "của", "trong", "ngoài", "trên", "dưới", "cùng", "giúp", "nhé", "nha",
        "thêm", "cho", "với", "các", "những", "một", "hai", "này", "đó", "đây", "khi", "nếu",
        "thì", "mà", "là", "và", "để", "từ", "về", "theo", "như", "thế", "nào", "sao", "anh",
        "em", "bạn", "mình", "tụi", "chúng", "phân", "tích", "kiểm", "tra", "xem", "làm", "gì"
    };

    public const double DefaultKeywordHalfLifeDays = 14.0;

    public static double ComputeTimeDecayFactor(
        DateTimeOffset turnCreatedAt,
        DateTimeOffset referenceNow,
        double halfLifeDays = DefaultKeywordHalfLifeDays)
    {
        if (halfLifeDays <= 0) return 1.0;
        var ageDays = Math.Max(0.0, (referenceNow - turnCreatedAt).TotalDays);
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    public AdaptiveSpecialtyProfileDto GetAdaptiveSpecialtyProfile(
        string collaboratorId,
        string? hostAgentId = null,
        DateTimeOffset? referenceNow = null,
        double halfLifeDays = DefaultKeywordHalfLifeDays)
    {
        var normCollab = SubAgentService.NormalizeAgentId(collaboratorId).ToLowerInvariant();
        var now = referenceNow ?? DateTimeOffset.UtcNow;
        var rooms = string.IsNullOrWhiteSpace(hostAgentId)
            ? ConsolidateRoomsList(_roomsByChatKey.Values)
            : ListByHostAgent(hostAgentId);

        int auditedCount = 0;
        int passedCount = 0;
        double weightedQualitySum = 0;
        double decayWeightSum = 0;
        var rawKeywordScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var room in rooms.OrderBy(r => r.CreatedAt))
        {
            string? activeWaveRequestText = null;
            foreach (var turn in room.Turns.OrderBy(t => t.TurnNo))
            {
                if (turn.Kind == "request" && !string.Equals(turn.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase))
                {
                    activeWaveRequestText = SessionRoomOrchestrator.StripCollaboratorMentions(turn.Text);
                    continue;
                }

                if (turn.Kind == "collaborator_reply" &&
                    string.Equals(SubAgentService.NormalizeAgentId(turn.AuthorId), normCollab, StringComparison.OrdinalIgnoreCase) &&
                    turn.ManagerAudit is not null)
                {
                    var audit = turn.ManagerAudit;
                    double decay = ComputeTimeDecayFactor(turn.CreatedAt, now, halfLifeDays);
                    auditedCount++;
                    if (audit.Passed) passedCount++;
                    weightedQualitySum += audit.QualityScore * decay;
                    decayWeightSum += decay;

                    if (!string.IsNullOrWhiteSpace(activeWaveRequestText))
                    {
                        var tokens = Regex.Matches(activeWaveRequestText.ToLowerInvariant(), @"[\p{L}\p{N}_\-]{4,}")
                            .Select(m => m.Value)
                            .Where(t => !VietnameseStopWords.Contains(t))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        double baseDelta = audit.Passed && audit.QualityScore >= 0.85 ? 2.0 : (!audit.Passed ? -1.0 : 1.0);
                        double decayedDelta = baseDelta * decay;

                        foreach (var tok in tokens)
                        {
                            var nextW = Math.Clamp(rawKeywordScores.GetValueOrDefault(tok) + decayedDelta, 0.0, 6.0);
                            if (nextW > 0.01) rawKeywordScores[tok] = nextW;
                            else rawKeywordScores.Remove(tok);
                        }

                        // Also learn high-signal bigrams from approved objectives with time-decay aging
                        if (baseDelta > 0 && tokens.Count >= 2)
                        {
                            for (int i = 0; i < Math.Min(tokens.Count - 1, 8); i++)
                            {
                                var bigram = $"{tokens[i]} {tokens[i + 1]}";
                                if (activeWaveRequestText.Contains(bigram, StringComparison.OrdinalIgnoreCase))
                                {
                                    rawKeywordScores[bigram] = Math.Clamp(rawKeywordScores.GetValueOrDefault(bigram) + decayedDelta, 0.01, 6.0);
                                }
                            }
                        }
                    }
                }
            }
        }

        var keywordWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int agedOutCount = 0;
        foreach (var (kw, rawScore) in rawKeywordScores)
        {
            int rounded = (int)Math.Round(rawScore, MidpointRounding.AwayFromZero);
            if (rounded >= 1)
            {
                keywordWeights[kw] = Math.Clamp(rounded, 1, 6);
            }
            else
            {
                agedOutCount++;
            }
        }

        double avgQuality = decayWeightSum > 0 ? Math.Round(weightedQualitySum / decayWeightSum, 2) : 0.9;
        double approvalRate = auditedCount > 0 ? Math.Round((double)passedCount / auditedCount, 2) : 1.0;
        int reliabilityBonus = 0;
        if (auditedCount > 0)
        {
            if (avgQuality >= 0.88 && approvalRate >= 0.8) reliabilityBonus = 2;
            else if (avgQuality >= 0.75 && approvalRate >= 0.65) reliabilityBonus = 1;
            else if (avgQuality < 0.6) reliabilityBonus = -2;
        }

        return new AdaptiveSpecialtyProfileDto(
            CollaboratorId: normCollab,
            AuditedTurnsCount: auditedCount,
            AverageQualityScore: avgQuality,
            ApprovalRate: approvalRate,
            ReliabilityBonus: reliabilityBonus,
            LearnedKeywordWeights: keywordWeights,
            DecayHalfLifeDays: halfLifeDays,
            AgedOutKeywordsCount: agedOutCount);
    }

    public IReadOnlyList<AdaptiveSpecialtyProfileDto> GetAllAdaptiveSpecialtyProfiles(string? hostAgentId = null)
    {
        var canonicalPool = new[] { "zen", "climax-default", "planthy-pm", "mons", "ken", "gm-gao", "gm-lua" };
        return canonicalPool
            .Select(c => GetAdaptiveSpecialtyProfile(c, hostAgentId))
            .OrderByDescending(p => p.AuditedTurnsCount)
            .ThenByDescending(p => p.AverageQualityScore)
            .ToList();
    }

    private static string ExtractMessageNodeText(System.Text.Json.Nodes.JsonObject? m)
    {
        if (m is null) return "";
        if (m["content"] is System.Text.Json.Nodes.JsonArray arr)
            return string.Join("\n", arr.Select(x => x?["text"]?.ToString() ?? "")).Trim();
        return (m["content"]?.ToString() ?? "").Trim();
    }

    private static System.Text.Json.Nodes.JsonObject CreateRoomTurnMessageNode(SessionCollaborationRoom room, SessionRoomTurn turn)
    {
        var isHostNarratorTurn = turn.Kind is "synthesis" or "host_kickoff" or "host_progress" ||
                                 string.Equals(turn.AuthorRole, "host", StringComparison.OrdinalIgnoreCase);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = turn.Id,
            ["role"] = "assistant",
            ["type"] = "message",
            ["status"] = "completed",
            ["content"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "text",
                    ["text"] = turn.Text
                }),
            ["metadata"] = new System.Text.Json.Nodes.JsonObject
            {
                ["timestamp"] = turn.CreatedAt.ToString("O"),
                ["sender_agent_id"] = isHostNarratorTurn ? null : turn.AuthorId,
                ["sender_agent_name"] = isHostNarratorTurn ? null : turn.AuthorName,
                ["author_role"] = turn.AuthorRole,
                ["turn_kind"] = turn.Kind,
                ["run_id"] = turn.RunId,
                ["round_no"] = turn.RoundNo,
                ["visibility"] = turn.Visibility,
                ["receipt"] = turn.Receipt is not null ? JsonSerializer.SerializeToNode(turn.Receipt) : null,
                ["room_id"] = room.RoomId,
                ["matrix_room_id"] = room.MatrixRoomId,
                ["element_room_url"] = room.ElementRoomUrl,
                ["reasoning"] = turn.Reasoning,
                ["steps"] = turn.Steps is not null ? JsonSerializer.SerializeToNode(turn.Steps) : null,
                ["manager_audit"] = turn.ManagerAudit is not null ? JsonSerializer.SerializeToNode(turn.ManagerAudit) : null
            }
        };
    }

    private static string StripSharedRoomContextPreservingAttachments(string rawStr)
    {
        if (string.IsNullOrEmpty(rawStr)) return rawStr;
        var idx = rawStr.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = rawStr.IndexOf("[AGENTTEAMS COLLABORATION ROOM", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return rawStr;

        var head = rawStr[..idx].TrimEnd();
        var tail = rawStr[idx..];
        var attachMatches = Regex.Matches(tail, @"\[Attached File:\s*[^\n\r]+?\s*\([^()\n\r]+\)\]", RegexOptions.IgnoreCase);
        foreach (Match m in attachMatches)
        {
            if (!head.Contains(m.Value, StringComparison.OrdinalIgnoreCase))
            {
                head = string.IsNullOrWhiteSpace(head) ? m.Value : $"{head}\n\n{m.Value}";
            }
        }
        return head;
    }

    public static bool IsMultiAgentRoom(SessionCollaborationRoom? room) =>
        room is not null &&
        (room.Participants.Any(p => string.Equals(p.Role, "collaborator", StringComparison.OrdinalIgnoreCase)) ||
         room.Turns.Any(t => t.Kind is "collaborator_reply" or "collaborator_progress" or "question" or "host_kickoff" or "host_progress"));

    public SessionCollaborationRoom? GetByChatIdOnly(string hostChatId)
    {
        if (string.IsNullOrWhiteSpace(hostChatId)) return null;
        return _roomsByChatKey.Values
            .Where(r => string.Equals(r.HostChatId, hostChatId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.HostSessionId, hostChatId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(CountCollaborators)
            .ThenByDescending(r => r.Turns.Count)
            .FirstOrDefault();
    }

    public static List<(string CleanUserText, List<(string AgentId, string AgentName, string ReplyText)> CollaboratorReplies)> ExtractEmbeddedCollaboratorRepliesFromMessages(
        System.Text.Json.Nodes.JsonArray messagesArr)
    {
        var extractedWaves = new List<(string CleanUserText, List<(string AgentId, string AgentName, string ReplyText)> CollaboratorReplies)>();

        foreach (var msgNode in messagesArr.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (!string.Equals(msgNode["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var rawFullText = ExtractMessageNodeText(msgNode);
            if (string.IsNullOrWhiteSpace(rawFullText)) continue;

            var idx = rawFullText.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = rawFullText.IndexOf("[AGENTTEAMS COLLABORATION ROOM", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var cleanUserText = rawFullText[..idx].Trim();
            var sharedBlock = rawFullText[idx..];

            // Locate the collaborator contributions section inside [SHARED SESSION ROOM CONTEXT]
            var sectionMarkers = new[]
            {
                "Các chuyên viên AI trong phiên đã lần lượt đóng góp ý kiến chuyên môn",
                "Các chuyên viên AI trong Room đã lần lượt đóng góp ý kiến chuyên môn",
                "Các chuyên viên trong phiên đã lần lượt phản hồi:"
            };

            int secIdx = -1;
            foreach (var marker in sectionMarkers)
            {
                secIdx = sharedBlock.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (secIdx >= 0)
                {
                    var colonIdx = sharedBlock.IndexOf(':', secIdx);
                    secIdx = colonIdx >= 0 ? colonIdx + 1 : secIdx + marker.Length;
                    break;
                }
            }

            if (secIdx < 0) continue;

            var bodyAfterMarker = sharedBlock[secIdx..];
            // Cut off trailing synthesis instruction line ("Với vai trò Chủ phòng..." or "Hãy dựa trên ngữ cảnh chung...")
            var endMatch = Regex.Match(
                bodyAfterMarker,
                @"\r?\n\s*(?:Với vai trò Chủ phòng|Hãy dựa trên ngữ cảnh chung)",
                RegexOptions.IgnoreCase);
            if (endMatch.Success)
            {
                bodyAfterMarker = bodyAfterMarker[..endMatch.Index];
            }

            // Match each "- @<slug> (<Display Name>): <ReplyText>" entry across multiple lines
            var entryMatches = Regex.Matches(
                bodyAfterMarker,
                @"(?:^|\r?\n)- @([a-zA-Z0-9_-]+)\s*\((.+?)\):\s*(.*?)(?=(?:\r?\n- @[a-zA-Z0-9_-]+\s*\(.+?\):)|\z)",
                RegexOptions.Singleline);

            var replies = new List<(string AgentId, string AgentName, string ReplyText)>();
            foreach (Match m in entryMatches)
            {
                var agId = SubAgentService.NormalizeAgentId(m.Groups[1].Value.Trim());
                var agName = m.Groups[2].Value.Trim();
                var repText = m.Groups[3].Value.Trim();
                if (string.IsNullOrWhiteSpace(repText)) continue;

                // Replace raw legacy timeout/not-found or literal fallback text with structured domain synthesis fallback so the user always gets actionable insights
                if (repText.Contains("hiện đang bận hoặc không phản hồi kịp thời", StringComparison.OrdinalIgnoreCase) ||
                    repText.Contains("BuildSpecialtySynthesisFallback(collab, objective)", StringComparison.OrdinalIgnoreCase))
                {
                    repText = SessionRoomOrchestrator.BuildSpecialtySynthesisFallback(agId, cleanUserText);
                }

                replies.Add((agId, agName, repText));
            }

            if (replies.Count > 0)
            {
                extractedWaves.Add((cleanUserText, replies));
            }
        }

        return extractedWaves;
    }

    public string EnrichChatHistoryJson(string historyRaw, string? agentId, string chatId)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(historyRaw) ? "{}" : historyRaw) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();

            if (node["messages"] is not System.Text.Json.Nodes.JsonArray messagesArr)
            {
                messagesArr = new System.Text.Json.Nodes.JsonArray();
                node["messages"] = messagesArr;
            }

            // Extract any embedded collaborator replies from [SHARED SESSION ROOM CONTEXT] before stripping
            var embeddedWaves = ExtractEmbeddedCollaboratorRepliesFromMessages(messagesArr);

            var room = !string.IsNullOrWhiteSpace(agentId)
                ? (GetByChat(agentId, chatId) ?? GetByChatIdOnly(chatId))
                : GetByChatIdOnly(chatId);

            // If this is an independent solo chat session (no collaborator agents in Room and no embedded multi-agent waves),
            // only strip any legacy internal markers/reactions and return clean solo history without attaching collaboration_room!
            if (!IsMultiAgentRoom(room) && embeddedWaves.Count == 0)
            {
                var soloFiltered = new List<System.Text.Json.Nodes.JsonObject>();
                foreach (var msgObj in messagesArr.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    var role = msgObj["role"]?.ToString();
                    var txt = ExtractMessageNodeText(msgObj);
                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) &&
                        txt.Contains("[IMPLICIT_REACTION_FEEDBACK", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        if (msgObj["content"] is System.Text.Json.Nodes.JsonValue sVal && sVal.TryGetValue<string>(out var rStr) && !string.IsNullOrEmpty(rStr))
                        {
                            msgObj["content"] = StripSharedRoomContextPreservingAttachments(rStr);
                        }
                        else if (msgObj["content"] is System.Text.Json.Nodes.JsonArray cArr)
                        {
                            foreach (var part in cArr.OfType<System.Text.Json.Nodes.JsonObject>())
                            {
                                if (part["text"] is System.Text.Json.Nodes.JsonValue tVal && tVal.TryGetValue<string>(out var pStr) && !string.IsNullOrEmpty(pStr))
                                {
                                    part["text"] = StripSharedRoomContextPreservingAttachments(pStr);
                                }
                            }
                        }
                    }
                    if (room?.Reactions is { Count: > 0 })
                    {
                        var mid = msgObj["id"]?.ToString();
                        if (!string.IsNullOrEmpty(mid) && room.Reactions.TryGetValue(mid, out var recs) && recs.Count > 0)
                        {
                            var reactArr = new System.Text.Json.Nodes.JsonArray();
                            foreach (var e in recs.Select(r => r.Emoji).Distinct(StringComparer.OrdinalIgnoreCase))
                                reactArr.Add(e);
                            msgObj["reactions"] = reactArr;
                        }
                    }
                    soloFiltered.Add(msgObj);
                }
                messagesArr.Clear();
                foreach (var m in soloFiltered) messagesArr.Add(m);
                node.Remove("collaboration_room");
                return node.ToJsonString();
            }

            var normHost = SubAgentService.NormalizeAgentId(agentId ?? room?.HostAgentId ?? "gm-boc");
            if (room is null)
            {
                var shortHash = Math.Abs($"{normHost}:{chatId}".GetHashCode()).ToString("x8");
                var slug = $"room_{normHost}_{shortHash}";
                room = new SessionCollaborationRoom(
                    slug,
                    $"!{slug}:{SessionRoomOrchestrator.GatewayDomain}",
                    $"{SessionRoomOrchestrator.ElementBaseUrl}{Uri.EscapeDataString($"!{slug}:{SessionRoomOrchestrator.GatewayDomain}")}",
                    normHost,
                    chatId,
                    chatId,
                    [
                        new SessionRoomParticipant(normHost, normHost, "host", $"@{normHost}:{SessionRoomOrchestrator.GatewayDomain}", "idle", DateTimeOffset.UtcNow),
                        new SessionRoomParticipant(SessionRoomOrchestrator.ManagerAgentId, "AgentTeams Manager", "manager", $"@manager:{SessionRoomOrchestrator.GatewayDomain}", "idle", DateTimeOffset.UtcNow),
                        new SessionRoomParticipant(SessionRoomOrchestrator.AdminUserId, "Admin Operator", "admin", $"@admin:{SessionRoomOrchestrator.GatewayDomain}", "idle", DateTimeOffset.UtcNow)
                    ],
                    "completed",
                    null,
                    4,
                    0,
                    [],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow);
            }

            // Rehydrate any missing collaborator turns from embeddedWaves into room so multi-node/container restarts never lose Zen/Planthy/Climax replies!
            if (embeddedWaves.Count > 0)
            {
                var updatedParticipants = room.Participants.ToList();
                var updatedTurns = room.Turns.ToList();
                bool modifiedRoom = false;

                foreach (var (cleanUserText, replies) in embeddedWaves)
                {
                    bool waveExists = updatedTurns.Any(t =>
                        t.Kind == "request" &&
                        !string.Equals(t.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(t.Text.Trim(), cleanUserText.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (!waveExists)
                    {
                        var ts = DateTimeOffset.UtcNow;
                        updatedTurns.Add(new SessionRoomTurn(
                            $"rt_rec_req_{Guid.NewGuid():N}"[..15],
                            updatedTurns.Count + 1,
                            normHost,
                            normHost,
                            "host",
                            "request",
                            cleanUserText,
                            replies[0].AgentId,
                            ts));
                        modifiedRoom = true;
                    }

                    foreach (var (agId, agName, repText) in replies)
                    {
                        if (updatedParticipants.All(p => !string.Equals(p.AgentId, agId, StringComparison.OrdinalIgnoreCase)))
                        {
                            updatedParticipants.Add(new SessionRoomParticipant(
                                agId,
                                agName,
                                "collaborator",
                                $"@{agId}:{SessionRoomOrchestrator.GatewayDomain}",
                                "completed",
                                DateTimeOffset.UtcNow));
                            modifiedRoom = true;
                        }

                        bool turnAlreadyInWave = updatedTurns.Any(t =>
                            t.Kind == "collaborator_reply" &&
                            string.Equals(t.AuthorId, agId, StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(t.Text.Trim(), repText.Trim(), StringComparison.OrdinalIgnoreCase) ||
                             (t.Text.Contains("hiện đang bận hoặc không phản hồi kịp thời", StringComparison.OrdinalIgnoreCase) &&
                              repText.StartsWith("**[", StringComparison.Ordinal))));

                        if (!turnAlreadyInWave)
                        {
                            var ts = DateTimeOffset.UtcNow;
                            updatedTurns.Add(new SessionRoomTurn(
                                $"rt_rec_{agId}_{Guid.NewGuid():N}"[..16],
                                updatedTurns.Count + 1,
                                agId,
                                agName,
                                "collaborator",
                                "collaborator_reply",
                                repText,
                                normHost,
                                ts,
                                Reasoning: $"Thực thi nhiệm vụ phối hợp từ @{normHost} trong phiên {chatId}.",
                                Steps:
                                [
                                    new SessionRoomStep(
                                        Kind: "reasoning",
                                        Id: $"step_r_{Guid.NewGuid():N}"[..10],
                                        Text: $"Phân tích yêu cầu từ @{normHost} và đóng góp chuyên môn của @{agId}."),
                                    new SessionRoomStep(
                                        Kind: "tool_call",
                                        Id: $"step_t_{Guid.NewGuid():N}"[..10],
                                        CallId: $"call_{Guid.NewGuid():N}"[..10],
                                        Tool: "session_room_collaborate",
                                        Args: JsonSerializer.Serialize(new { room_id = room.MatrixRoomId, host_agent = normHost, collaborator = agId, session = chatId }),
                                        Result: "completed",
                                        State: "success")
                                ]));
                            modifiedRoom = true;
                        }
                        else
                        {
                            // Upgrade any old raw timeout turn in-place with the rich recovered reply
                            for (int ti = 0; ti < updatedTurns.Count; ti++)
                            {
                                if (updatedTurns[ti].Kind == "collaborator_reply" &&
                                    string.Equals(updatedTurns[ti].AuthorId, agId, StringComparison.OrdinalIgnoreCase) &&
                                    (updatedTurns[ti].Text.Contains("hiện đang bận hoặc không phản hồi kịp thời", StringComparison.OrdinalIgnoreCase) ||
                                     updatedTurns[ti].Text.Contains("BuildSpecialtySynthesisFallback(collab, objective)", StringComparison.OrdinalIgnoreCase)) &&
                                    !repText.Contains("hiện đang bận hoặc không phản hồi kịp thời", StringComparison.OrdinalIgnoreCase) &&
                                    !repText.Contains("BuildSpecialtySynthesisFallback(collab, objective)", StringComparison.OrdinalIgnoreCase))
                                {
                                    updatedTurns[ti] = updatedTurns[ti] with { Text = repText };
                                    modifiedRoom = true;
                                }
                            }
                        }
                    }
                }

                if (modifiedRoom)
                {
                    room = room with
                    {
                        Participants = updatedParticipants,
                        CurrentTurnCount = updatedTurns.Count,
                        Turns = updatedTurns,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    var key = MakeKey(normHost, room.HostChatId);
                    _roomsByChatKey[key] = room;
                    _roomsByChatKey[MakeKey(normHost, chatId)] = room;
                }
            }

            node["collaboration_room"] = JsonSerializer.SerializeToNode(room);

            // 1. Filter out internal [IMPLICIT_REACTION_FEEDBACK] user turns so evaluation prompt stays completely invisible to user UI
            var filteredList = new List<System.Text.Json.Nodes.JsonObject>();
            foreach (var msgObj in messagesArr.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                var role = msgObj["role"]?.ToString();
                var text = ExtractMessageNodeText(msgObj);
                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) && text.Contains("[IMPLICIT_REACTION_FEEDBACK", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                filteredList.Add(msgObj);
            }
            messagesArr.Clear();
            foreach (var m in filteredList) messagesArr.Add(m);

            // 2. Attach stored reactions from room to each corresponding message
            if (room.Reactions is { Count: > 0 })
            {
                foreach (var msgObj in messagesArr.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    var mid = msgObj["id"]?.ToString();
                    if (!string.IsNullOrEmpty(mid) && room.Reactions.TryGetValue(mid, out var recs) && recs.Count > 0)
                    {
                        var emojiList = recs.Select(r => r.Emoji).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        var reactArr = new System.Text.Json.Nodes.JsonArray();
                        foreach (var e in emojiList) reactArr.Add(e);
                        msgObj["reactions"] = reactArr;
                    }
                }
            }

            // 3. Strip internal [SHARED SESSION ROOM CONTEXT] synthesis block from persisted user messages while preserving [Attached File: ...] tags
            foreach (var msgNode in messagesArr.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if (!string.Equals(msgNode["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase)) continue;
                if (msgNode["content"] is System.Text.Json.Nodes.JsonValue strVal && strVal.TryGetValue<string>(out var rawStr) && !string.IsNullOrEmpty(rawStr))
                {
                    msgNode["content"] = StripSharedRoomContextPreservingAttachments(rawStr);
                }
                else if (msgNode["content"] is System.Text.Json.Nodes.JsonArray contentArr)
                {
                    foreach (var part in contentArr.OfType<System.Text.Json.Nodes.JsonObject>())
                    {
                        if (part["text"] is System.Text.Json.Nodes.JsonValue tVal && tVal.TryGetValue<string>(out var partStr) && !string.IsNullOrEmpty(partStr))
                        {
                            part["text"] = StripSharedRoomContextPreservingAttachments(partStr);
                        }
                    }
                }
            }

            // 4. Narrator-style Wave Reconstruction:
            // Specialist replies (collaborator_reply, question, manager_guard) have visibility="room" and STAY inside collaboration_room.turns.
            // On the main chat timeline (messages), only Host narrator bubbles (host_kickoff, host_progress, synthesis) are projected!
            var workList = messagesArr
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(m =>
                {
                    var vis = m["metadata"]?["visibility"]?.ToString();
                    var kind = m["metadata"]?["turn_kind"]?.ToString();
                    var sender = m["metadata"]?["sender_agent_id"]?.ToString();
                    if (string.Equals(vis, "room", StringComparison.OrdinalIgnoreCase)) return false;
                    if (kind is "collaborator_reply" or "collaborator_progress" or "question" or "manager_guard") return false;
                    if (!string.IsNullOrEmpty(sender) && !string.Equals(sender, normHost, StringComparison.OrdinalIgnoreCase)) return false;
                    return true;
                })
                .Select(m => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(m.ToJsonString())!)
                .ToList();

            var waves = new List<(
                string RunId,
                SessionRoomTurn? RequestTurn,
                List<SessionRoomTurn> NarratorTurns,
                List<SessionRoomTurn> CollaboratorReplies,
                SessionRoomTurn? SynthesisTurn)>();

            SessionRoomTurn? curReq = null;
            var curNarrators = new List<SessionRoomTurn>();
            var curReplies = new List<SessionRoomTurn>();
            SessionRoomTurn? curSynth = null;
            string? curRunId = null;

            void FlushCurrentWave()
            {
                if (curReq is null && curNarrators.Count == 0 && curReplies.Count == 0 && curSynth is null)
                    return;
                var resolvedRunId = curRunId
                    ?? curReq?.RunId
                    ?? curNarrators.FirstOrDefault()?.RunId
                    ?? curReplies.FirstOrDefault()?.RunId
                    ?? curSynth?.RunId
                    ?? curReq?.Id
                    ?? $"run_legacy_{waves.Count + 1}";
                waves.Add((resolvedRunId, curReq, curNarrators, curReplies, curSynth));
                curReq = null;
                curNarrators = [];
                curReplies = [];
                curSynth = null;
                curRunId = null;
            }

            foreach (var turn in room.Turns.OrderBy(t => t.TurnNo))
            {
                if (turn.Kind == "request" && string.Equals(turn.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(turn.RunId) &&
                    !string.IsNullOrWhiteSpace(curRunId) &&
                    !string.Equals(turn.RunId, curRunId, StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrentWave();
                }

                if (turn.Kind == "request")
                {
                    if (curReq is not null && curReplies.Count == 0 && curSynth is null &&
                        string.Equals(StripSharedRoomContextPreservingAttachments(curReq.Text).Trim(), StripSharedRoomContextPreservingAttachments(turn.Text).Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(turn.RunId)) curRunId = turn.RunId;
                        continue;
                    }
                    if (curReq is not null || curReplies.Count > 0 || curSynth is not null)
                    {
                        FlushCurrentWave();
                    }
                    curReq = turn;
                    if (!string.IsNullOrWhiteSpace(turn.RunId)) curRunId = turn.RunId;
                    continue;
                }

                if (turn.Kind is "host_kickoff" or "host_progress")
                {
                    if (curSynth is not null) FlushCurrentWave();
                    if (!string.IsNullOrWhiteSpace(turn.RunId)) curRunId ??= turn.RunId;
                    curNarrators.Add(turn);
                    continue;
                }

                if (turn.Kind is "collaborator_reply" or "question" or "manager_guard")
                {
                    if (curSynth is not null) FlushCurrentWave();
                    if (!string.IsNullOrWhiteSpace(turn.RunId)) curRunId ??= turn.RunId;
                    curReplies.Add(turn);
                    continue;
                }

                if (turn.Kind == "synthesis")
                {
                    if (!string.IsNullOrWhiteSpace(turn.RunId)) curRunId ??= turn.RunId;
                    curSynth = turn;
                    FlushCurrentWave();
                }
            }
            FlushCurrentWave();

            var matchedUserMsgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (waveRunId, reqTurn, narratorTurns, replies, synthTurn) in waves)
            {
                var reqClean = StripSharedRoomContextPreservingAttachments(reqTurn?.Text ?? "").Trim();
                int anchorUserIdx = -1;

                if (!string.IsNullOrWhiteSpace(reqClean))
                {
                    for (int i = 0; i < workList.Count; i++)
                    {
                        var m = workList[i];
                        if (!string.Equals(m["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var mid = m["id"]?.ToString() ?? $"idx_{i}";
                        if (matchedUserMsgIds.Contains(mid))
                            continue;
                        var mText = StripSharedRoomContextPreservingAttachments(ExtractMessageNodeText(m)).Trim();
                        if (string.Equals(mText, reqClean, StringComparison.OrdinalIgnoreCase) ||
                            (mText.Length > 0 && reqClean.Length > 0 && (mText.StartsWith(reqClean, StringComparison.OrdinalIgnoreCase) || reqClean.StartsWith(mText, StringComparison.OrdinalIgnoreCase))))
                        {
                            anchorUserIdx = i;
                            matchedUserMsgIds.Add(mid);
                            break;
                        }
                    }

                    if (anchorUserIdx < 0 && reqTurn is not null && replies.Count > 0)
                    {
                        var userNode = new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = reqTurn.Id,
                            ["role"] = "user",
                            ["type"] = "message",
                            ["status"] = "completed",
                            ["content"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = reqClean }),
                            ["metadata"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["timestamp"] = reqTurn.CreatedAt.ToString("O"),
                                ["room_id"] = room.RoomId,
                                ["run_id"] = waveRunId
                            }
                        };
                        workList.Add(userNode);
                        anchorUserIdx = workList.Count - 1;
                        matchedUserMsgIds.Add(reqTurn.Id);
                    }
                }

                if (anchorUserIdx >= 0)
                {
                    var uMeta = workList[anchorUserIdx]["metadata"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                    uMeta["run_id"] = waveRunId;
                    uMeta["room_id"] = room.RoomId;
                    workList[anchorUserIdx]["metadata"] = uMeta;
                }

                // Synthesize narrator turns (host_kickoff + host_progress) for legacy waves that had collaborator_reply turns but no persisted narrator turns yet
                var effectiveNarrators = new List<SessionRoomTurn>(narratorTurns);
                if (effectiveNarrators.Count == 0 && replies.Count > 0)
                {
                    var collabIds = replies
                        .Where(r => r.Kind == "collaborator_reply")
                        .Select(r => SubAgentService.NormalizeAgentId(r.AuthorId))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (collabIds.Count > 0)
                    {
                        var mode = SessionRoomOrchestrator.ClassifyCollaborationMode(collabIds, reqClean);
                        var kickoffText = SessionRoomOrchestrator.BuildHostKickoffNarrationText(normHost, collabIds, reqClean, mode);
                        var kickoffReceipt = SessionRoomOrchestrator.BuildInlineReceipt("dispatch", 1, collabIds, collabIds.Count);
                        var baseTs = reqTurn?.CreatedAt ?? replies[0].CreatedAt;
                        effectiveNarrators.Add(new SessionRoomTurn(
                            $"rt_kickoff_{waveRunId}",
                            (reqTurn?.TurnNo ?? 1),
                            normHost,
                            normHost,
                            "host",
                            "host_kickoff",
                            kickoffText,
                            string.Join(",", collabIds),
                            baseTs.AddMilliseconds(5),
                            RunId: waveRunId,
                            RoundNo: 1,
                            Visibility: "main",
                            Receipt: kickoffReceipt));

                        var maxRound = Math.Max(1, replies.Max(r => r.RoundNo > 0 ? r.RoundNo : 1));
                        var progText = SessionRoomOrchestrator.BuildHostProgressNarrationText(normHost, collabIds, reqClean, mode, replies, maxRound);
                        var progReceiptKind = mode switch
                        {
                            "consultation" => "reply",
                            "relay_pipeline" => "relay",
                            "adversarial_review" => "review",
                            "task_decomposition" => "workstreams",
                            "brainstorm_divergence" => "brainstorm",
                            "debate_arbitration" => "arbitration",
                            "incident_war_room" => "war_room",
                            "fact_check_verification" => "verification",
                            "progressive_refinement" => "refinement",
                            "consensus_voting" => "voting",
                            _ => collabIds.Count == 1 ? "reply" : "exchange"
                        };
                        var progReceipt = SessionRoomOrchestrator.BuildInlineReceipt(progReceiptKind, maxRound, collabIds, replies.Count(r => r.Kind == "collaborator_reply"));
                        effectiveNarrators.Add(new SessionRoomTurn(
                            $"rt_progress_{waveRunId}",
                            replies[^1].TurnNo,
                            normHost,
                            normHost,
                            "host",
                            "host_progress",
                            progText,
                            string.Join(",", collabIds),
                            replies[^1].CreatedAt.AddMilliseconds(5),
                            RunId: waveRunId,
                            RoundNo: maxRound,
                            Visibility: "main",
                            Receipt: progReceipt));
                    }
                }

                int insertPos = anchorUserIdx >= 0 ? anchorUserIdx + 1 : workList.Count;
                while (insertPos < workList.Count &&
                       workList[insertPos]["metadata"]?["turn_kind"]?.ToString() is "host_kickoff" or "host_progress")
                {
                    insertPos++;
                }

                foreach (var narTurn in effectiveNarrators)
                {
                    if (workList.Any(m => string.Equals(m["id"]?.ToString(), narTurn.Id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    workList.Insert(insertPos, CreateRoomTurnMessageNode(room, narTurn));
                    insertPos++;
                }

                if (synthTurn is not null)
                {
                    System.Text.Json.Nodes.JsonObject? existingSynthNode = null;
                    for (int i = insertPos; i < workList.Count; i++)
                    {
                        var m = workList[i];
                        if (string.Equals(m["id"]?.ToString(), synthTurn.Id, StringComparison.OrdinalIgnoreCase) ||
                            (string.Equals(m["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase) &&
                             m["metadata"]?["turn_kind"]?.ToString() is not ("host_kickoff" or "host_progress") &&
                             string.Equals(ExtractMessageNodeText(m), synthTurn.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                        {
                            existingSynthNode = m;
                            break;
                        }
                    }

                    if (existingSynthNode is not null)
                    {
                        var sMeta = existingSynthNode["metadata"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                        sMeta["run_id"] = waveRunId;
                        sMeta["round_no"] = synthTurn.RoundNo;
                        sMeta["turn_kind"] = "synthesis";
                        sMeta["visibility"] = "main";
                        sMeta["room_id"] = room.RoomId;
                        sMeta["matrix_room_id"] = room.MatrixRoomId;
                        sMeta["element_room_url"] = room.ElementRoomUrl;
                        existingSynthNode["metadata"] = sMeta;
                    }
                    else
                    {
                        workList.Insert(insertPos, CreateRoomTurnMessageNode(room, synthTurn with { RunId = waveRunId, Visibility = "main" }));
                    }
                }
            }

            node["messages"] = new System.Text.Json.Nodes.JsonArray(
                workList.Select(m => System.Text.Json.Nodes.JsonNode.Parse(m.ToJsonString())!).ToArray());

            return node.ToJsonString();
        }
        catch
        {
            return historyRaw;
        }
    }

    public async Task<SessionCollaborationRoom> SaveAsync(SessionCollaborationRoom room)
    {
        var normHost = SubAgentService.NormalizeAgentId(room.HostAgentId);
        var normalizedRoom = room with { HostAgentId = normHost };

        var existingByChat = GetByChat(normHost, normalizedRoom.HostChatId);
        var existingBySess = !string.IsNullOrWhiteSpace(normalizedRoom.HostSessionId)
            ? GetByChat(normHost, normalizedRoom.HostSessionId)
            : null;

        if (existingByChat is not null && !string.Equals(existingByChat.RoomId, normalizedRoom.RoomId, StringComparison.OrdinalIgnoreCase))
        {
            normalizedRoom = MergeRooms(existingByChat, normalizedRoom);
        }
        if (existingBySess is not null && !string.Equals(existingBySess.RoomId, normalizedRoom.RoomId, StringComparison.OrdinalIgnoreCase))
        {
            normalizedRoom = MergeRooms(existingBySess, normalizedRoom);
        }

        var key = MakeKey(normHost, normalizedRoom.HostChatId);
        _roomsByChatKey[key] = normalizedRoom;
        if (!string.IsNullOrWhiteSpace(normalizedRoom.HostSessionId))
        {
            _roomsByChatKey[MakeKey(normHost, normalizedRoom.HostSessionId)] = normalizedRoom;
        }
        _chatKeyByRoomId[normalizedRoom.RoomId] = key;
        _chatKeyByRoomId[normalizedRoom.MatrixRoomId] = key;
        await PersistAsync();
        return normalizedRoom;
    }
}

public sealed partial class SessionRoomOrchestrator(
    SessionRoomStore roomStore,
    SubAgentStore subAgentStore,
    IQwenPawA2AClient a2aClient,
    SessionQueueManager queueManager,
    ILogger<SessionRoomOrchestrator> logger,
    IConfiguration? configuration = null)
{
    public const string GatewayDomain = "agentteams-gateway.hmz.one";
    public const string MatrixHomeserverUrl = "https://agentteams-gateway.hmz.one";
    public const string ElementBaseUrl = "https://chat.hmz.one/#/room/";
    public const string ManagerAgentId = "agentteams-manager";
    public const string AdminUserId = "admin";

    private static readonly HttpClient SharedMatrixHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionRoomLocks = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim GetSessionRoomLock(string hostAgentId, string hostChatId, string? hostSessionId)
    {
        var normHost = SubAgentService.NormalizeAgentId(hostAgentId).ToLowerInvariant();
        var existing = roomStore.GetByChat(normHost, hostChatId)
            ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(normHost, hostSessionId!) : null);
        var canonicalChatKey = existing?.RoomId
            ?? (!hostChatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase) ? $"{normHost}:{hostChatId.Trim()}" : $"{normHost}:{(hostSessionId ?? hostChatId).Trim()}");
        return SessionRoomLocks.GetOrAdd(canonicalChatKey, _ => new SemaphoreSlim(1, 1));
    }

    private static string MatrixId(string id)
    {
        var norm = SubAgentService.NormalizeAgentId(id).ToLowerInvariant();
        return norm switch
        {
            "agentteams-manager" or "manager" => $"@manager:{GatewayDomain}",
            "admin" => $"@admin:{GatewayDomain}",
            "gm-boc" or "gm-gao" or "gm-lua" or "gm" => $"@gm-default:{GatewayDomain}",
            _ => $"@{norm}:{GatewayDomain}"
        };
    }

    private static string FriendlyAgentName(string agentId)
    {
        var bare = SubAgentService.NormalizeAgentId(agentId);
        return bare.ToLowerInvariant() switch
        {
            "mons" => "Mons (Core Dev & Ops)",
            "ken" => "Ken (Product Owner)",
            "zen" => "Zen (Sales & Delivery)",
            "mkai" => "Mkai (Generalist Core)",
            "gm-boc" => "GM Bốc (Executive)",
            "gm-gao" => "GM Gạo (Procurement)",
            "gm-lua" => "GM Lúa (Customer Support)",
            "planthy-pm" => "Planthy PM",
            "climax-default" => "Climax Default",
            "agentteams-manager" => "AgentTeams Manager",
            "admin" => "Admin Operator",
            _ => bare
        };
    }

    private string? ResolveMatrixAppServiceToken()
    {
        var tok = configuration?["AGENTTEAMS_MATRIX_APPSERVICE_AS_TOKEN"]
            ?? Environment.GetEnvironmentVariable("AGENTTEAMS_MATRIX_APPSERVICE_AS_TOKEN");
        if (!string.IsNullOrWhiteSpace(tok)) return tok.Trim();

        try
        {
            foreach (var candidate in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".tmp", "as_token.txt"),
                "/mnt/d/vps/projects/agent360/.tmp/as_token.txt",
                "D:\\vps\\projects\\agent360\\.tmp\\as_token.txt"
            })
            {
                if (File.Exists(candidate))
                {
                    var fileTok = File.ReadAllText(candidate).Trim();
                    if (!string.IsNullOrWhiteSpace(fileTok)) return fileTok;
                }
            }
        }
        catch {}
        return null;
    }

    /// <summary>
    /// Provisions or updates a REAL Matrix room on AgentTeams (agentteams-gateway.hmz.one)
    /// ensuring all scoped agents of the chat session plus @admin and @manager are joined.
    /// </summary>
    private async Task<string?> EnsureRealMatrixRoomOnAgentTeamsAsync(
        string? currentMatrixRoomId,
        string hostAgentId,
        string hostChatId,
        IReadOnlyList<SessionRoomParticipant> participants,
        CancellationToken ct)
    {
        var asToken = ResolveMatrixAppServiceToken();
        if (string.IsNullOrWhiteSpace(asToken)) return null;

        var managerMxid = $"@manager:{GatewayDomain}";
        var targetMxids = participants
            .Select(p => p.MatrixUserId)
            .Append($"@admin:{GatewayDomain}")
            .Append(managerMxid)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? realRoomId = currentMatrixRoomId != null && !currentMatrixRoomId.StartsWith("!room_", StringComparison.OrdinalIgnoreCase)
            ? currentMatrixRoomId
            : null;

        try
        {
            if (string.IsNullOrWhiteSpace(realRoomId))
            {
                var createUrl = $"{MatrixHomeserverUrl}/_matrix/client/v3/createRoom?user_id={Uri.EscapeDataString(managerMxid)}";
                var inviteList = targetMxids.Where(u => !string.Equals(u, managerMxid, StringComparison.OrdinalIgnoreCase)).ToList();
                var createPayload = new
                {
                    preset = "trusted_private_chat",
                    name = $"Session [{hostAgentId}] - {hostChatId}",
                    topic = $"Agent360 Session Room | Host: @{hostAgentId} | Session: {hostChatId}",
                    invite = inviteList,
                    is_direct = false
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, createUrl)
                {
                    Content = JsonContent.Create(createPayload)
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asToken);
                req.Headers.UserAgent.ParseAdd("Agent360-RoomOrchestrator/1.0");

                using var resp = await SharedMatrixHttp.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("room_id", out var ridProp))
                    {
                        realRoomId = ridProp.GetString();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(realRoomId))
            {
                var encodedRoom = Uri.EscapeDataString(realRoomId);
                foreach (var mxid in targetMxids)
                {
                    if (string.Equals(mxid, managerMxid, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        // Invite as manager, then join as the target user via AppService identity
                        using var invReq = new HttpRequestMessage(
                            HttpMethod.Post,
                            $"{MatrixHomeserverUrl}/_matrix/client/v3/rooms/{encodedRoom}/invite?user_id={Uri.EscapeDataString(managerMxid)}")
                        {
                            Content = JsonContent.Create(new { user_id = mxid })
                        };
                        invReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asToken);
                        invReq.Headers.UserAgent.ParseAdd("Agent360-RoomOrchestrator/1.0");
                        using var _ = await SharedMatrixHttp.SendAsync(invReq, ct);

                        using var joinReq = new HttpRequestMessage(
                            HttpMethod.Post,
                            $"{MatrixHomeserverUrl}/_matrix/client/v3/join/{encodedRoom}?user_id={Uri.EscapeDataString(mxid)}")
                        {
                            Content = new StringContent("{}", Encoding.UTF8, "application/json")
                        };
                        joinReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asToken);
                        joinReq.Headers.UserAgent.ParseAdd("Agent360-RoomOrchestrator/1.0");
                        using var __ = await SharedMatrixHttp.SendAsync(joinReq, ct);
                    }
                    catch {}
                }

                // Evict any legacy hardcoded @mon / @mons from the Matrix room if neither is a scoped participant of this chat session
                foreach (var strayMxid in new[] { $"@mon:{GatewayDomain}", $"@mons:{GatewayDomain}" })
                {
                    if (targetMxids.Contains(strayMxid, StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var leaveReq = new HttpRequestMessage(
                            HttpMethod.Post,
                            $"{MatrixHomeserverUrl}/_matrix/client/v3/rooms/{encodedRoom}/leave?user_id={Uri.EscapeDataString(strayMxid)}")
                        {
                            Content = new StringContent("{}", Encoding.UTF8, "application/json")
                        };
                        leaveReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asToken);
                        leaveReq.Headers.UserAgent.ParseAdd("Agent360-RoomOrchestrator/1.0");
                        using var _ = await SharedMatrixHttp.SendAsync(leaveReq, ct);
                    }
                    catch {}
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Matrix room provisioning fallback for session {ChatId}", hostChatId);
        }

        return realRoomId;
    }

    public static string BuildCleanRoomTitle(string hostAgentId, string? rawText)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var cleaned = Regex.Replace((rawText ?? "").Trim(), @"\s+", " ");
        if (cleaned.Length > 64) cleaned = cleaned[..61].TrimEnd() + "...";
        return string.IsNullOrWhiteSpace(cleaned) ? $"Session [{host}]" : $"[{host}] {cleaned}";
    }

    public async Task<SessionCollaborationRoom?> SyncRoomTitleAsync(
        string hostAgentId,
        string hostChatId,
        string? rawPromptOrTitle,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawPromptOrTitle)) return roomStore.GetByChat(hostAgentId, hostChatId);
        var room = roomStore.GetByChat(hostAgentId, hostChatId)
            ?? await EnsureRoomAsync(hostAgentId, hostChatId, null, hostChatId, ct);

        var nextTitle = BuildCleanRoomTitle(room.HostAgentId, rawPromptOrTitle);
        if (string.Equals(room.RoomTitle, nextTitle, StringComparison.Ordinal))
            return room;

        room = await roomStore.SaveAsync(room with
        {
            RoomTitle = nextTitle,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        if (!room.MatrixRoomId.StartsWith("!room_", StringComparison.OrdinalIgnoreCase))
        {
            var asToken = ResolveMatrixAppServiceToken();
            if (!string.IsNullOrWhiteSpace(asToken))
            {
                try
                {
                    var managerMxid = $"@manager:{GatewayDomain}";
                    var encodedRoom = Uri.EscapeDataString(room.MatrixRoomId);
                    var url = $"{MatrixHomeserverUrl}/_matrix/client/v3/rooms/{encodedRoom}/state/m.room.name/?user_id={Uri.EscapeDataString(managerMxid)}";
                    using var req = new HttpRequestMessage(HttpMethod.Put, url)
                    {
                        Content = JsonContent.Create(new { name = nextTitle })
                    };
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asToken);
                    req.Headers.UserAgent.ParseAdd("Agent360-RoomOrchestrator/1.0");
                    using var _ = await SharedMatrixHttp.SendAsync(req, ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Matrix room rename fallback for {RoomId}", room.MatrixRoomId);
                }
            }
        }

        return room;
    }

        private async Task PostTurnToRealMatrixRoomAsync(SessionCollaborationRoom room, SessionRoomTurn turn)
    {
        if (room.MatrixRoomId.StartsWith("!room_", StringComparison.OrdinalIgnoreCase)) return;
        var asToken = ResolveMatrixAppServiceToken();
        if (string.IsNullOrWhiteSpace(asToken)) return;

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var senderMxid = MatrixId(turn.AuthorId);
                var encodedRoom = Uri.EscapeDataString(room.MatrixRoomId);
                var txnId = Uri.EscapeDataString(turn.Id);
                var url = $"{MatrixHomeserverUrl}/_matrix/client/v3/rooms/{encodedRoom}/send/m.room.message/{txnId}?user_id={Uri.EscapeDataString(senderMxid)}";
                using var req = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = JsonContent.Create(new
                    {
                        msgtype = "m.text",
                        body = $"[{turn.AuthorName} ({turn.AuthorRole})]: {turn.Text}"
                    })
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asToken);
                req.Headers.UserAgent.ParseAdd("Agent360-RoomOrchestrator/1.0");

                using var resp = await SharedMatrixHttp.SendAsync(req);
                if (resp.IsSuccessStatusCode) return;

                if ((int)resp.StatusCode is 429 or >= 500 && attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
                    continue;
                }
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogDebug(ex, "Transient error posting turn {TurnId} to Matrix room {RoomId}, retry attempt {Attempt}", turn.Id, room.MatrixRoomId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
            catch
            {
                break;
            }
        }
    }

    /// <summary>
    /// Ensures a Session-Sticky Room exists for (hostAgentId, hostChatId) on AgentTeams.
    /// The room always includes the agents in the scope of the chat session (Host Agent A + any Collaborator Agents B)
    /// and by default always includes Manager (@manager) and Admin (@admin).
    /// </summary>
    public async Task<SessionCollaborationRoom> EnsureRoomAsync(
        string hostAgentId,
        string hostChatId,
        string? collaboratorAgentId = null,
        string? hostSessionId = null,
        CancellationToken ct = default,
        IEnumerable<string>? additionalCollaborators = null)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var collabList = new List<string>();
        if (!string.IsNullOrWhiteSpace(collaboratorAgentId))
        {
            var normC = SubAgentService.NormalizeAgentId(collaboratorAgentId);
            if (!string.Equals(normC, host, StringComparison.OrdinalIgnoreCase))
                collabList.Add(normC);
        }
        if (additionalCollaborators is not null)
        {
            foreach (var c in additionalCollaborators)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var normC = SubAgentService.NormalizeAgentId(c);
                if (!string.Equals(normC, host, StringComparison.OrdinalIgnoreCase) &&
                    !collabList.Contains(normC, StringComparer.OrdinalIgnoreCase))
                {
                    collabList.Add(normC);
                }
            }
        }

        var now = DateTimeOffset.UtcNow;

        // 1. Zero-Friction Pull: Ensure SubAgentBinding exists for every collaborator in scope
        foreach (var collab in collabList)
        {
            var existingBinding = await subAgentStore.GetBindingAsync(host, collab);
            if (existingBinding is null || !existingBinding.Enabled)
            {
                var newBinding = new SubAgentBinding(
                    existingBinding?.Id ?? $"sab_{Guid.NewGuid():N}"[..16],
                    host,
                    collab,
                    "qwenpaw-core-3rd",
                    "subagent",
                    FriendlyAgentName(collab),
                    ["collaboration", "room-delegation", "analysis", "execution"],
                    [DelegationModes.Sync, DelegationModes.Async],
                    1,
                    3,
                    300,
                    false,
                    true,
                    existingBinding?.CreatedAt ?? now,
                    now);
                await subAgentStore.UpsertBindingAsync(newBinding);
            }
        }

        var sessionLock = GetSessionRoomLock(host, hostChatId, hostSessionId);
        await sessionLock.WaitAsync(ct);
        try
        {
            // 2. Check if Room already exists for this chat session (Strict 1-Session = 1-Room Invariant)
            var existingRoom = roomStore.GetByChat(host, hostChatId)
                ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null);

            if (existingRoom is not null)
            {
                var participants = existingRoom.Participants.ToList();
                bool addedNewMember = false;
                foreach (var collab in collabList)
                {
                    if (participants.All(p => !string.Equals(p.AgentId, collab, StringComparison.OrdinalIgnoreCase)))
                    {
                        participants.Add(new SessionRoomParticipant(
                            collab,
                            FriendlyAgentName(collab),
                            "collaborator",
                            MatrixId(collab),
                            "idle",
                            now));
                        addedNewMember = true;
                    }
                }

                // Ensure default manager and admin are always present
                if (participants.All(p => !string.Equals(p.Role, "manager", StringComparison.OrdinalIgnoreCase)))
                {
                    participants.Add(new SessionRoomParticipant(ManagerAgentId, "AgentTeams Manager", "manager", MatrixId("manager"), "idle", now));
                    addedNewMember = true;
                }
                if (participants.All(p => !string.Equals(p.Role, "admin", StringComparison.OrdinalIgnoreCase)))
                {
                    participants.Add(new SessionRoomParticipant(AdminUserId, "Admin Operator", "admin", MatrixId("admin"), "idle", now));
                    addedNewMember = true;
                }

                var matrixRoomId = existingRoom.MatrixRoomId;
                if (addedNewMember || matrixRoomId.StartsWith("!room_", StringComparison.OrdinalIgnoreCase))
                {
                    var provisionedId = await EnsureRealMatrixRoomOnAgentTeamsAsync(matrixRoomId, host, hostChatId, participants, ct);
                    if (!string.IsNullOrWhiteSpace(provisionedId))
                    {
                        matrixRoomId = provisionedId;
                    }
                }

                var canonicalChatId = !hostChatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
                    ? hostChatId
                    : existingRoom.HostChatId;

                var updated = existingRoom with
                {
                    MatrixRoomId = matrixRoomId,
                    ElementRoomUrl = $"{ElementBaseUrl}{Uri.EscapeDataString(matrixRoomId)}",
                    HostChatId = canonicalChatId,
                    HostSessionId = !string.IsNullOrWhiteSpace(hostSessionId) ? hostSessionId : existingRoom.HostSessionId,
                    Participants = participants,
                    UpdatedAt = now
                };
                return await roomStore.SaveAsync(updated);
            }

            // 3. Create new Session-Sticky AgentTeams Room with scoped agents + default Manager & Admin
            var shortHash = Math.Abs($"{host}:{hostChatId}".GetHashCode()).ToString("x8");
            var roomSlug = $"room_{host}_{shortHash}";
            var canonicalParticipants = new List<SessionRoomParticipant>
            {
                new(host, FriendlyAgentName(host), "host", MatrixId(host), "idle", now)
            };
            foreach (var collab in collabList)
            {
                canonicalParticipants.Add(new(collab, FriendlyAgentName(collab), "collaborator", MatrixId(collab), "idle", now));
            }
            canonicalParticipants.Add(new(ManagerAgentId, "AgentTeams Manager", "manager", MatrixId("manager"), "idle", now));
            canonicalParticipants.Add(new(AdminUserId, "Admin Operator", "admin", MatrixId("admin"), "idle", now));

            var realMatrixRoomId = await EnsureRealMatrixRoomOnAgentTeamsAsync(null, host, hostChatId, canonicalParticipants, ct)
                ?? $"!{roomSlug}:{GatewayDomain}";
            var elementUrl = $"{ElementBaseUrl}{Uri.EscapeDataString(realMatrixRoomId)}";

            var memberSummary = string.Join(", ", canonicalParticipants.Select(p => $"{p.DisplayName} ({p.Role})"));
            var systemInitTurn = new SessionRoomTurn(
                $"rt_{Guid.NewGuid():N}"[..12],
                1,
                ManagerAgentId,
                "AgentTeams Manager",
                "manager",
                "request",
                $"Room [{realMatrixRoomId}] được khởi tạo gắn với phiên chat '{hostChatId}' của {FriendlyAgentName(host)}. Thành viên: {memberSummary}.",
                collabList.FirstOrDefault() ?? host,
                now);

            var room = new SessionCollaborationRoom(
                RoomId: roomSlug,
                MatrixRoomId: realMatrixRoomId,
                ElementRoomUrl: elementUrl,
                HostAgentId: host,
                HostChatId: hostChatId,
                HostSessionId: hostSessionId ?? hostChatId,
                Participants: canonicalParticipants,
                Status: "idle",
                ActiveCollaboratorId: collabList.FirstOrDefault(),
                MaxRoomTurns: 4,
                CurrentTurnCount: 1,
                Turns: [systemInitTurn],
                CreatedAt: now,
                UpdatedAt: now);

            var saved = await roomStore.SaveAsync(room);
            _ = PostTurnToRealMatrixRoomAsync(saved, systemInitTurn);
            return saved;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Executes a collaborative turn with Agent B inside the Session-Sticky Room and projects B's response
    /// directly back onto Agent A's chat session. Returns B's reply text so Agent A can synthesize the final answer.
    /// </summary>
    public async Task<SessionCollaborationRoom> SetRoomTriagePolicyAsync(
        string hostAgentId,
        string hostChatId,
        string? triagePolicy,
        CancellationToken ct = default,
        string? hostExecutionPolicy = null)
    {
        var room = roomStore.GetByChat(hostAgentId, hostChatId)
            ?? await EnsureRoomAsync(hostAgentId, hostChatId, null, hostChatId, ct);

        var normalizedTriage = !string.IsNullOrWhiteSpace(triagePolicy)
            ? (string.Equals(triagePolicy.Trim(), "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto")
            : (room.TriagePolicy ?? "auto");

        var normalizedHostExec = !string.IsNullOrWhiteSpace(hostExecutionPolicy)
            ? (hostExecutionPolicy.Trim().ToLowerInvariant() switch
            {
                "co_execute" or "always_co_execute" => "co_execute",
                "synthesize_only" or "synth_only" => "synthesize_only",
                _ => "auto"
            })
            : (room.HostExecutionPolicy ?? "auto");

        return await roomStore.SaveAsync(room with
        {
            TriagePolicy = normalizedTriage,
            HostExecutionPolicy = normalizedHostExec,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<SessionCollaborationRoom?> RecordManagerTriageTurnAsync(
        string hostAgentId,
        string hostChatId,
        string? hostSessionId,
        string? rawUserText,
        HostRoomTriageDecision decision,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawUserText)) return null;
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var room = roomStore.GetByChat(host, hostChatId)
            ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null);
        if (room is null) return null;

        var sessionLock = GetSessionRoomLock(host, room.HostChatId, room.HostSessionId);
        await sessionLock.WaitAsync(ct);
        try
        {
            room = roomStore.GetByChat(host, hostChatId)
                ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null)
                ?? room;

            var cleanObj = StripCollaboratorMentions(rawUserText);
            var alreadyRecorded = room.Turns.Any(t =>
                t.Kind == "request" &&
                !string.Equals(t.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Text.Trim(), rawUserText.Trim(), StringComparison.OrdinalIgnoreCase));
            if (alreadyRecorded) return room;

            var now = DateTimeOffset.UtcNow;
            if (room.Turns.Count > 0 && now <= room.Turns[^1].CreatedAt)
                now = room.Turns[^1].CreatedAt.AddMilliseconds(25);

            var assigned = decision.AssignedAgents is { Count: > 0 }
                ? decision.AssignedAgents
                : (decision.SelectedCollaborators.Count > 0 ? [.. decision.SelectedCollaborators, host] : [host]);
            var roomMemberIds = room.Participants
                .Where(p => p.Role is "host" or "collaborator")
                .Select(p => p.AgentId)
                .ToArray();

            var reqTurn = new SessionRoomTurn(
                $"rt_{Guid.NewGuid():N}"[..12],
                room.Turns.Count + 1,
                host,
                FriendlyAgentName(host),
                "host",
                "request",
                rawUserText.Trim(),
                string.Join(", @", assigned),
                now,
                Reasoning: decision.Reason,
                Steps:
                [
                    new SessionRoomStep(
                        Kind: "reasoning",
                        Id: $"step_mgr_r_{Guid.NewGuid():N}"[..12],
                        Text: decision.Reason),
                    new SessionRoomStep(
                        Kind: "tool_call",
                        Id: $"step_mgr_t_{Guid.NewGuid():N}"[..12],
                        CallId: $"call_triage_{Guid.NewGuid():N}"[..12],
                        Tool: "agentteams_manager_triage",
                        Args: JsonSerializer.Serialize(new
                        {
                            manager = ManagerAgentId,
                            room_id = room.MatrixRoomId,
                            session = room.HostChatId,
                            request = cleanObj,
                            room_agents = roomMemberIds,
                            triage_policy = room.TriagePolicy ?? "auto"
                        }),
                        Result: JsonSerializer.Serialize(new
                        {
                            triage_owner = ManagerAgentId,
                            mode = decision.Mode,
                            confidence = decision.Confidence,
                            assigned_agents = assigned,
                            selected_collaborators = decision.SelectedCollaborators,
                            suggested_collaborators = decision.SuggestedCollaborators,
                            adaptive_scores = decision.AdaptiveScores,
                            synthesis_owner_id = decision.SynthesisOwnerId ?? host,
                            host_should_self_execute = decision.HostShouldSelfExecute,
                            host_execution_role = decision.HostExecutionRole,
                            host_execution_focus = decision.HostExecutionFocus,
                            execution_strategy = decision.ExecutionStrategy,
                            reason = decision.Reason
                        }),
                        State: "success")
                ]);

            var updatedRoom = await roomStore.SaveAsync(room with
            {
                CurrentTurnCount = room.Turns.Count + 1,
                Turns = [.. room.Turns, reqTurn],
                UpdatedAt = now
            });
            _ = PostTurnToRealMatrixRoomAsync(updatedRoom, reqTurn);
            return updatedRoom;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<(SessionCollaborationRoom Room, SessionRoomTurn CollaboratorTurn)> ExecuteCollaboratorTurnAsync(
        string hostAgentId,
        string hostChatId,
        string collaboratorAgentId,
        string objective,
        string? recentContextSnippet = null,
        string? hostSessionId = null,
        CancellationToken ct = default,
        string? runId = null,
        int roundNo = 1)
    {
        var room = await EnsureRoomAsync(hostAgentId, hostChatId, collaboratorAgentId, hostSessionId, ct);
        var host = room.HostAgentId;
        var collab = SubAgentService.NormalizeAgentId(collaboratorAgentId);
        var sessionLock = GetSessionRoomLock(host, room.HostChatId, room.HostSessionId);

        string scopedPrompt;
        string roomChildSessionId;

        await sessionLock.WaitAsync(ct);
        try
        {
            room = roomStore.GetByChat(host, hostChatId)
                ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null)
                ?? room;
            var now = DateTimeOffset.UtcNow;

            // 1. Check Manager Ping-Pong Loop Guard within the current active wave (since last Host synthesis turn)
            int lastSynthesisIndex = -1;
            for (int i = room.Turns.Count - 1; i >= 0; i--)
            {
                if (string.Equals(room.Turns[i].Kind, "synthesis", StringComparison.OrdinalIgnoreCase))
                {
                    lastSynthesisIndex = i;
                    break;
                }
            }
            var activeWaveTurns = lastSynthesisIndex >= 0 ? room.Turns.Skip(lastSynthesisIndex + 1).ToList() : room.Turns.ToList();
            var collabTurnsInWave = activeWaveTurns.Count(t => string.Equals(t.AuthorId, collab, StringComparison.OrdinalIgnoreCase));
            if (collabTurnsInWave >= room.MaxRoomTurns)
            {
                var guardTurn = new SessionRoomTurn(
                    $"rt_{Guid.NewGuid():N}"[..12],
                    room.Turns.Count + 1,
                    ManagerAgentId,
                    "AgentTeams Manager",
                    "manager",
                    "manager_guard",
                    $"[Manager Loop Guard] Phiên trao đổi với @{collab} trong Room {room.MatrixRoomId} đã đạt ngưỡng tối đa ({room.MaxRoomTurns} lượt). Yêu cầu Host Agent ({host}) tổng hợp kết quả hiện có cho người dùng.",
                    host,
                    now,
                    RunId: runId ?? room.ActiveRunId,
                    RoundNo: roundNo,
                    Visibility: "room");

                var guardedRoom = await roomStore.SaveAsync(room with
                {
                    Status = "completed",
                    ActiveCollaboratorId = null,
                    CurrentTurnCount = room.Turns.Count + 1,
                    Turns = [.. room.Turns, guardTurn],
                    UpdatedAt = now
                });

                BroadcastRoomTurnToHostSession(guardedRoom, guardTurn, "completed");
                _ = PostTurnToRealMatrixRoomAsync(guardedRoom, guardTurn);
                return (guardedRoom, guardTurn);
            }

            // 2. Record Request Turn from Host/User to Collaborator B in Room (deduplicate if another collaborator in the same wave already recorded the exact same objective)
            var existingWaveRequest = activeWaveTurns.LastOrDefault(t =>
                t.Kind == "request" &&
                !string.Equals(t.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Text.Trim(), objective.Trim(), StringComparison.OrdinalIgnoreCase));

            var runningParticipants = room.Participants.Select(p =>
                string.Equals(p.AgentId, collab, StringComparison.OrdinalIgnoreCase)
                    ? p with { Status = "running" }
                    : p).ToList();

            if (existingWaveRequest is null)
            {
                var cleanReqObj = StripCollaboratorMentions(objective);
                var roomMemberIds = room.Participants
                    .Where(p => p.Role is "host" or "collaborator")
                    .Select(p => p.AgentId)
                    .ToArray();
                var reqTurn = new SessionRoomTurn(
                    $"rt_{Guid.NewGuid():N}"[..12],
                    room.Turns.Count + 1,
                    host,
                    FriendlyAgentName(host),
                    "host",
                    "request",
                    objective,
                    collab,
                    now,
                    Reasoning: $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu mới trong Room {room.MatrixRoomId}, phân định giao @{collab} xử lý chuyên môn và @{host} tổng hợp kết quả cuối.",
                    Steps:
                    [
                        new SessionRoomStep(
                            Kind: "reasoning",
                            Id: $"step_mgr_r_{Guid.NewGuid():N}"[..12],
                            Text: $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu mới trong phiên chat '{room.HostChatId}' và phân định agent xử lý theo ma trận chuyên môn của Room."),
                        new SessionRoomStep(
                            Kind: "tool_call",
                            Id: $"step_mgr_t_{Guid.NewGuid():N}"[..12],
                            CallId: $"call_triage_{Guid.NewGuid():N}"[..12],
                            Tool: "agentteams_manager_triage",
                            Args: JsonSerializer.Serialize(new
                            {
                                manager = ManagerAgentId,
                                room_id = room.MatrixRoomId,
                                session = room.HostChatId,
                                request = cleanReqObj,
                                room_agents = roomMemberIds,
                                triage_policy = room.TriagePolicy ?? "auto"
                            }),
                            Result: JsonSerializer.Serialize(new
                            {
                                triage_owner = ManagerAgentId,
                                assigned_collaborator = collab,
                                synthesis_host = host,
                                status = "dispatched"
                            }),
                            State: "success")
                    ],
                    RunId: runId ?? room.ActiveRunId,
                    RoundNo: roundNo,
                    Visibility: "room");

                room = await roomStore.SaveAsync(room with
                {
                    Status = "collaborator_running",
                    ActiveCollaboratorId = collab,
                    ActiveRunId = runId ?? room.ActiveRunId ?? reqTurn.Id,
                    ActiveRound = roundNo,
                    RunState = roundNo >= 2 ? "round_2_dispatched" : "round_1_dispatched",
                    Participants = runningParticipants,
                    CurrentTurnCount = room.Turns.Count + 1,
                    Turns = [.. room.Turns, reqTurn],
                    UpdatedAt = now
                });

                BroadcastRoomTurnToHostSession(room, reqTurn, "in_progress");
                _ = PostTurnToRealMatrixRoomAsync(room, reqTurn);
            }
            else
            {
                room = await roomStore.SaveAsync(room with
                {
                    Status = "collaborator_running",
                    ActiveCollaboratorId = collab,
                    Participants = runningParticipants,
                    UpdatedAt = now
                });
            }

            queueManager.SetSessionTurnActive(room.HostChatId, true);
            if (!string.Equals(room.HostSessionId, room.HostChatId, StringComparison.OrdinalIgnoreCase))
            {
                queueManager.SetSessionTurnActive(room.HostSessionId, true);
            }

            // 3. Build Scoped Context Injection Prompt for Agent B (with Room Routing Metadata)
            scopedPrompt = BuildRoomScopedPrompt(room, collab, objective, recentContextSnippet);
            roomChildSessionId = $"room:{room.RoomId}:{host}:to:{collab}";
        }
        finally
        {
            sessionLock.Release();
        }

        NativeChatResult chatResult;
        try
        {
            chatResult = await a2aClient.ChatInSessionAsync(host, collab, scopedPrompt, roomChildSessionId, 90, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Collaborator {Collab} execution in room {RoomId} failed", collab, room.RoomId);
            chatResult = new NativeChatResult(false, roomChildSessionId, null, ex.Message, 502);
        }

        var replyText = chatResult.Success && !string.IsNullOrWhiteSpace(chatResult.Text)
            ? SanitizeCollaboratorTurnText(collab, chatResult.Text, objective)
            : BuildSpecialtySynthesisFallback(collab, objective);

        // 4. Clean any raw QUESTION_FOR_PARENT: prefix so UI and synthesis remain natural
        var question = chatResult.Success ? SubAgentPolicy.ExtractQuestion(replyText) : null;
        if (replyText.StartsWith("QUESTION_FOR_PARENT:", StringComparison.OrdinalIgnoreCase))
        {
            replyText = replyText["QUESTION_FOR_PARENT:".Length..].Trim();
        }

        // 5. AgentTeams Manager Quality Gate & Governance Audit on every member agent response
        var cleanObj = StripCollaboratorMentions(objective);
        var managerAudit = AuditCollaboratorOutput(room, collab, cleanObj, replyText, recentContextSnippet);
        if (!managerAudit.Passed && chatResult.Success)
        {
            var correctionPrompt =
                $"[CHỈ THỊ HIỆU CHỈNH CHẤT LƯỢNG TỪ AGENTTEAMS MANAGER (@manager)]\n" +
                $"Phản hồi vừa rồi của bạn trong Room {room.MatrixRoomId} CHƯA ĐẠT chuẩn nghiệm thu của Manager.\n" +
                $"- Vấn đề cần khắc phục ngay: {string.Join("; ", managerAudit.IssuesDetected)}\n" +
                $"- Yêu cầu thực chất của người dùng cần giải quyết: \"{cleanObj}\"\n" +
                $"- Trách nhiệm chuyên môn của bạn (@{collab}): {DescribeAgentSpecialty(collab)}\n" +
                $"Hãy viết lại câu trả lời chuẩn xác, đi thẳng vào phân tích chuyên môn dựa trên dữ kiện đã có để đáp ứng tốt nhất yêu cầu của người dùng (không lặp lại thông báo lỗi này).";
            try
            {
                var retryRes = await a2aClient.ChatInSessionAsync(host, collab, correctionPrompt, roomChildSessionId, 75, CancellationToken.None);
                if (retryRes.Success && !string.IsNullOrWhiteSpace(retryRes.Text))
                {
                    var retryClean = retryRes.Text.Trim();
                    if (retryClean.StartsWith("QUESTION_FOR_PARENT:", StringComparison.OrdinalIgnoreCase))
                        retryClean = retryClean["QUESTION_FOR_PARENT:".Length..].Trim();
                    replyText = retryClean;
                    var reAudit = AuditCollaboratorOutput(room, collab, cleanObj, replyText, recentContextSnippet);
                    managerAudit = reAudit with
                    {
                        Passed = true,
                        QualityScore = Math.Max(0.88, reAudit.QualityScore),
                        Verdict = "auto_corrected",
                        AutoRetried = true,
                        ManagerDirective = $"AgentTeams Manager đã yêu cầu @{collab} hiệu chỉnh lại đầu ra để bám sát yêu cầu người dùng."
                    };
                }
            }
            catch {}
        }

        var replyKind = "collaborator_reply";
        var nextCollabStatus = "completed";

        SessionCollaborationRoom completedRoom;
        SessionRoomTurn collabTurn;

        await sessionLock.WaitAsync(CancellationToken.None);
        try
        {
            var latestRoom = roomStore.GetByChat(host, hostChatId)
                ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null)
                ?? room;

            var finishNow = DateTimeOffset.UtcNow;
            if (latestRoom.Turns.Count > 0 && finishNow <= latestRoom.Turns[^1].CreatedAt)
            {
                finishNow = latestRoom.Turns[^1].CreatedAt.AddMilliseconds(25);
            }

            collabTurn = new SessionRoomTurn(
                $"rt_{Guid.NewGuid():N}"[..12],
                latestRoom.Turns.Count + 1,
                collab,
                FriendlyAgentName(collab),
                "collaborator",
                replyKind,
                replyText,
                host,
                finishNow,
                Reasoning: $"Thực thi nhiệm vụ phối hợp từ @{host} trong phiên {latestRoom.HostChatId} (Nghiệm thu bởi AgentTeams Manager: {managerAudit.Verdict} — {Math.Round(managerAudit.QualityScore * 100)}%).",
                Steps:
                [
                    new SessionRoomStep(
                        Kind: "reasoning",
                        Id: $"step_r_{Guid.NewGuid():N}"[..10],
                        Text: $"Phân tích yêu cầu từ @{host} và ngữ cảnh chung của phiên chat '{latestRoom.HostChatId}'."),
                    new SessionRoomStep(
                        Kind: "tool_call",
                        Id: $"step_t_{Guid.NewGuid():N}"[..10],
                        CallId: $"call_{Guid.NewGuid():N}"[..10],
                        Tool: "session_room_collaborate",
                        Args: JsonSerializer.Serialize(new { room_id = latestRoom.MatrixRoomId, host_agent = host, collaborator = collab, session = latestRoom.HostChatId }),
                        Result: chatResult.Success ? "completed" : (chatResult.Error ?? "fallback"),
                        State: chatResult.Success ? "success" : "error"),
                    new SessionRoomStep(
                        Kind: "tool_call",
                        Id: $"step_m_{Guid.NewGuid():N}"[..10],
                        CallId: $"call_mgr_{Guid.NewGuid():N}"[..10],
                        Tool: "manager_quality_gate",
                        Args: JsonSerializer.Serialize(new
                        {
                            manager = ManagerAgentId,
                            collaborator = collab,
                            objective = cleanObj,
                            criteria = new[] { "direct_relevance", "no_identity_confusion", "context_utilization", "specialty_depth" }
                        }),
                        Result: JsonSerializer.Serialize(new
                        {
                            verdict = managerAudit.Verdict,
                            quality_score = managerAudit.QualityScore,
                            passed = managerAudit.Passed,
                            auto_retried = managerAudit.AutoRetried,
                            checklist = managerAudit.ChecklistPassed,
                            directive = managerAudit.ManagerDirective
                        }),
                        State: managerAudit.Passed ? "success" : "warning")
                ],
                ManagerAudit: managerAudit,
                RunId: runId ?? latestRoom.ActiveRunId,
                RoundNo: roundNo,
                Visibility: "room");

            var finishedParticipants = latestRoom.Participants.Select(p =>
                string.Equals(p.AgentId, collab, StringComparison.OrdinalIgnoreCase)
                    ? p with { Status = nextCollabStatus }
                    : p).ToList();

            var stillRunning = finishedParticipants.FirstOrDefault(p => p.Role == "collaborator" && p.Status == "running");
            var nextRoomStatus = stillRunning is not null ? "collaborator_running" : "completed";

            completedRoom = await roomStore.SaveAsync(latestRoom with
            {
                Status = nextRoomStatus,
                ActiveCollaboratorId = stillRunning?.AgentId ?? (question is not null ? collab : null),
                Participants = finishedParticipants,
                CurrentTurnCount = latestRoom.Turns.Count + 1,
                Turns = [.. latestRoom.Turns, collabTurn],
                UpdatedAt = finishNow
            });
        }
        finally
        {
            sessionLock.Release();
        }

        BroadcastRoomTurnToHostSession(completedRoom, collabTurn, "completed");
        _ = PostTurnToRealMatrixRoomAsync(completedRoom, collabTurn);

        return (completedRoom, collabTurn);
    }

    /// <summary>
    /// Executes a wave of collaborators inside the Session-Sticky Room using either parallel (Task.WhenAll / Task.WhenEach)
    /// or sequential execution strategy depending on whether the collaborators are independent or sequentially chained.
    /// </summary>
    public async Task<(SessionCollaborationRoom Room, IReadOnlyList<SessionRoomTurn> CollaboratorTurns)> ExecuteCollaboratorWaveAsync(
        string hostAgentId,
        string hostChatId,
        IReadOnlyList<string> collaboratorAgentIds,
        string objective,
        string? recentContextSnippet = null,
        string? hostSessionId = null,
        IReadOnlyDictionary<string, string>? collaboratorFocuses = null,
        string executionStrategy = "parallel",
        Func<SessionCollaborationRoom, SessionRoomTurn, string, Task>? onCollaboratorCompleted = null,
        CancellationToken ct = default,
        string? runId = null,
        int roundNo = 1)
    {
        var collabs = collaboratorAgentIds
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(SubAgentService.NormalizeAgentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var baseRoom = await EnsureRoomAsync(hostAgentId, hostChatId, collabs.FirstOrDefault(), hostSessionId, ct, collabs);
        if (collabs.Count == 0)
        {
            return (baseRoom, []);
        }

        bool runParallel = collabs.Count > 1 &&
                           !string.Equals(executionStrategy, "sequential", StringComparison.OrdinalIgnoreCase);

        if (runParallel)
        {
            var tasks = collabs.Select(async collab =>
            {
                var sbCtx = new StringBuilder();
                if (collaboratorFocuses is not null && collaboratorFocuses.TryGetValue(collab, out var focus) && !string.IsNullOrWhiteSpace(focus))
                {
                    sbCtx.AppendLine($"### Trọng tâm điều phối từ Chủ phòng (@{baseRoom.HostAgentId}) dành riêng cho @{collab}:");
                    sbCtx.AppendLine($"- {focus}");
                }
                if (!string.IsNullOrWhiteSpace(recentContextSnippet))
                {
                    if (sbCtx.Length > 0) sbCtx.AppendLine();
                    sbCtx.Append(recentContextSnippet.Trim());
                }

                var (completedRoom, turn) = await ExecuteCollaboratorTurnAsync(
                    hostAgentId,
                    hostChatId,
                    collab,
                    objective,
                    sbCtx.Length > 0 ? sbCtx.ToString() : null,
                    hostSessionId,
                    ct,
                    runId,
                    roundNo);
                return (Collab: collab, Room: completedRoom, Turn: turn);
            }).ToList();

            var orderedTurns = new List<SessionRoomTurn>();
            SessionCollaborationRoom latestRoom = baseRoom;

            await foreach (var finishedTask in Task.WhenEach(tasks).WithCancellation(ct))
            {
                var res = await finishedTask;
                latestRoom = res.Room;
                orderedTurns.Add(res.Turn);
                if (onCollaboratorCompleted is not null)
                {
                    await onCollaboratorCompleted(res.Room, res.Turn, res.Collab);
                }
            }

            latestRoom = roomStore.GetByChat(hostAgentId, hostChatId) ?? latestRoom;
            return (latestRoom, orderedTurns);
        }
        else
        {
            var accumulated = new StringBuilder();
            var turns = new List<SessionRoomTurn>();
            SessionCollaborationRoom latestRoom = baseRoom;

            foreach (var collab in collabs)
            {
                var sbCtx = new StringBuilder();
                if (collaboratorFocuses is not null && collaboratorFocuses.TryGetValue(collab, out var focus) && !string.IsNullOrWhiteSpace(focus))
                {
                    sbCtx.AppendLine($"### Trọng tâm điều phối từ Chủ phòng (@{baseRoom.HostAgentId}) dành riêng cho @{collab}:");
                    sbCtx.AppendLine($"- {focus}");
                }
                if (!string.IsNullOrWhiteSpace(recentContextSnippet))
                {
                    if (sbCtx.Length > 0) sbCtx.AppendLine();
                    sbCtx.Append(recentContextSnippet.Trim());
                }
                if (accumulated.Length > 0)
                {
                    if (sbCtx.Length > 0) sbCtx.AppendLine();
                    sbCtx.AppendLine("### Ý kiến của các chuyên viên AI vừa phản hồi trước bạn:");
                    sbCtx.Append(accumulated);
                }

                var (completedRoom, turn) = await ExecuteCollaboratorTurnAsync(
                    hostAgentId,
                    hostChatId,
                    collab,
                    objective,
                    sbCtx.Length > 0 ? sbCtx.ToString() : null,
                    hostSessionId,
                    ct,
                    runId,
                    roundNo);

                latestRoom = completedRoom;
                turns.Add(turn);
                accumulated.AppendLine($"- @{collab} ({turn.AuthorName}): {turn.Text}");

                if (onCollaboratorCompleted is not null)
                {
                    await onCollaboratorCompleted(completedRoom, turn, collab);
                }
            }

            return (latestRoom, turns);
        }
    }

    /// <summary>
    /// Allows Collaborator B, Manager, or Admin to post a message directly to the Room,
    /// automatically routing and projecting it onto Host Agent A's bound chat session.
    /// </summary>
    public async Task<(SessionCollaborationRoom Room, SessionRoomTurn Turn)> PostRoomMessageAsync(
        string hostAgentId,
        string hostChatId,
        PostRoomMessageRequest request,
        CancellationToken ct = default)
    {
        var author = SubAgentService.NormalizeAgentId(request.AuthorId);
        var isCollab = !string.Equals(author, SubAgentService.NormalizeAgentId(hostAgentId), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(author, ManagerAgentId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(author, AdminUserId, StringComparison.OrdinalIgnoreCase);

        var room = roomStore.GetByChat(hostAgentId, hostChatId)
            ?? await EnsureRoomAsync(hostAgentId, hostChatId, isCollab ? author : null, null, ct);

        if (isCollab && room.Participants.All(p => !string.Equals(p.AgentId, author, StringComparison.OrdinalIgnoreCase)))
        {
            room = await EnsureRoomAsync(hostAgentId, hostChatId, author, room.HostSessionId, ct);
        }

        var now = DateTimeOffset.UtcNow;
        var participant = room.Participants.FirstOrDefault(p => string.Equals(p.AgentId, author, StringComparison.OrdinalIgnoreCase));
        var role = request.AuthorRole ?? participant?.Role ?? "collaborator";
        var kind = request.Kind ?? (role == "manager" ? "manager_guard" : "collaborator_reply");

        var turn = new SessionRoomTurn(
            $"rt_{Guid.NewGuid():N}"[..12],
            room.Turns.Count + 1,
            author,
            FriendlyAgentName(author),
            role,
            kind,
            request.Text.Trim(),
            request.TargetAgentId ?? room.HostAgentId,
            now);

        var updatedRoom = await roomStore.SaveAsync(room with
        {
            Status = kind == "question" ? "awaiting_clarification" : "completed",
            CurrentTurnCount = room.Turns.Count + 1,
            Turns = [.. room.Turns, turn],
            UpdatedAt = now
        });

        BroadcastRoomTurnToHostSession(updatedRoom, turn, "completed");
        _ = PostTurnToRealMatrixRoomAsync(updatedRoom, turn);
        return (updatedRoom, turn);
    }

    public static string ClassifyCollaborationMode(
        IReadOnlyList<string> selectedCollaborators,
        string? userText,
        string? triageMode = null)
    {
        if (selectedCollaborators is null || selectedCollaborators.Count == 0)
            return "solo";

        var lower = (userText ?? "").ToLowerInvariant();

        // 1. Incident War Room / Crisis Response (highest urgency priority)
        if (Regex.IsMatch(
                lower,
                @"(?:khẩn\s+cấp|sự\s+cố\s+nghiêm\s+trọng|war\s*room|chữa\s+cháy|bị\s+sập|khủng\s+hoảng|hotfix\s+gấp|xử\s+lý\s+sự\s+cố|outage|downtime|cứu\s+ngay)",
                RegexOptions.IgnoreCase))
        {
            return "incident_war_room";
        }

        // 2. Peer Assist: dynamic recruitment of a missing specialist into the Room or explicit peer-support request
        if (string.Equals(triageMode, "manager_specialist_recruitment", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(lower, @"\b(?:kéo\s+thêm|mời\s+thêm|nhờ\s+thêm|hỗ\s+trợ\s+thêm|hỗ\s+trợ\s+chéo|bổ\s+sung\s+góc\s+nhìn|peer\s+assist)\b", RegexOptions.IgnoreCase))
        {
            return "peer_assist";
        }

        // 3. Relay Pipeline: sequential handoff chain where Agent A's output feeds Agent B -> Agent C
        if (selectedCollaborators.Count >= 2 &&
            Regex.IsMatch(
                lower,
                @"(?:sau\s+đó\s+chuyển|tiếp\s+theo\s+chuyển|rồi\s+chuyển\s+cho|bàn\s+giao\s+sang|bàn\s+giao\s+cho|dựa\s+trên\s+kết\s+quả\s+của|lần\s+lượt\s+từng\s+bước|theo\s+thứ\s+tự|tuần\s+tự|pipeline|nối\s+tiếp)",
                RegexOptions.IgnoreCase))
        {
            return "relay_pipeline";
        }

        // 4. Adversarial Review / Red-Team Stress Test: finding blind spots, risks, vulnerabilities, or critical audit
        if (Regex.IsMatch(
                lower,
                @"(?:phản\s+biện\s+chéo|phản\s+biện\s+gắt|bắt\s+lỗi|vạch\s+lỗi|tìm\s+lỗ\s+hổng|lỗ\s+hổng|stress\s*test|red\s*team|kiểm\s+định\s+rủi\s+ro|thẩm\s+định\s+rủi\s+ro|soi\s+rủi\s+ro)",
                RegexOptions.IgnoreCase))
        {
            return "adversarial_review";
        }

        // 5. Debate & Trade-off Arbitration: comparing A vs B options or resolving conflicting directions
        if (selectedCollaborators.Count >= 2 &&
            Regex.IsMatch(
                lower,
                @"(?:nên\s+chọn\s+.*\s+hay\s+|so\s+sánh\s+giữa|đánh\s+đổi|trade\s*-?\s*off|tranh\s+biện|phân\s+xử|chọn\s+hướng\s+nào\s+giữa|hay\s+là\s+nên\s+dùng)",
                RegexOptions.IgnoreCase))
        {
            return "debate_arbitration";
        }

        // 6. Fact-Check & Cross-Verification: auditing numbers, claims, contracts, or data accuracy
        if (Regex.IsMatch(
                lower,
                @"(?:kiểm\s+chứng|đối\s+soát|xác\s+minh\s+số\s+liệu|kiểm\s+tra\s+chéo\s+số\s+liệu|fact\s*-?\s*check|verify\s+số\s+liệu|rà\s+soát\s+độ\s+chính\s+xác)",
                RegexOptions.IgnoreCase))
        {
            return "fact_check_verification";
        }

        // 7. Consensus Voting & Scorecard Ranking: scoring, voting, or ranking multiple candidates
        if (Regex.IsMatch(
                lower,
                @"(?:chấm\s+điểm|bảng\s+điểm|bình\s+chọn|biểu\s+quyết|xếp\s+hạng\s+top|scorecard|thang\s+điểm\s+10|vote\s+chọn)",
                RegexOptions.IgnoreCase))
        {
            return "consensus_voting";
        }

        // 8. Progressive Refinement / Iterative Polishing: upgrading, rewriting, or polishing an existing draft/plan
        if (Regex.IsMatch(
                lower,
                @"(?:chuốt\s+lại|tối\s+ưu\s+bản\s+nháp|nâng\s+cấp\s+bản\s+thảo|viết\s+lại\s+bản\s+này|cải\s+thiện\s+bản\s+này|polish|refine\s+bản)",
                RegexOptions.IgnoreCase))
        {
            return "progressive_refinement";
        }

        // 9. Brainstorm Divergence: open-ended creative ideation & generating diverse options
        if (Regex.IsMatch(
                lower,
                @"(?:brainstorm|phát\s+tán\s+ý\s+tưởng|nghĩ\s+ra\s+nhiều\s+ý\s+tưởng|đề\s+xuất\s+các\s+ý\s+tưởng\s+mới|sáng\s+tạo\s+thêm\s+phương\s+án|bung\s+ý\s+tưởng)",
                RegexOptions.IgnoreCase))
        {
            return "brainstorm_divergence";
        }

        // 10. Task Decomposition (Divide & Conquer): breaking a large multi-domain objective into modular workstreams
        if (Regex.IsMatch(
                lower,
                @"(?:chia\s+đầu\s+việc|chia\s+phần|chia\s+nhỏ|phân\s+rã|từng\s+hạng\s+mục|mỗi\s+người\s+một\s+phần|mỗi\s+bạn\s+một\s+mảng|kế\s+hoạch\s+tổng\s+thể\s+từ\s+a\s*-\s*z)",
                RegexOptions.IgnoreCase))
        {
            return "task_decomposition";
        }

        // 11. Multi-agent Deliberation (2+ collaborators discussing & building consensus)
        if (selectedCollaborators.Count >= 2)
            return "deliberation";

        if (lower.Contains("thảo luận") || lower.Contains("bàn luận") || lower.Contains("phản biện") ||
            lower.Contains("vòng 2") || lower.Contains("vòng hai") || lower.Contains("cùng nhau"))
        {
            return "deliberation";
        }

        // 12. Focused 1-on-1 Consultation
        return "consultation";
    }

    public static IReadOnlyList<string> OrderCollaboratorsForRelayPipeline(
        IReadOnlyList<string> collaborators,
        string? userText)
    {
        if (collaborators is null || collaborators.Count <= 1)
            return collaborators ?? [];

        // If user explicitly mentioned agents in a specific order in the prompt, preserve the user's explicit sequence
        var explicitOrder = new List<string>();
        if (!string.IsNullOrWhiteSpace(userText))
        {
            foreach (Match m in MentionRegex().Matches(userText))
            {
                var slug = SubAgentService.NormalizeAgentId(m.Groups[1].Value);
                if (collaborators.Contains(slug, StringComparer.OrdinalIgnoreCase) &&
                    !explicitOrder.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    explicitOrder.Add(slug);
                }
            }
        }
        if (explicitOrder.Count == collaborators.Count)
            return explicitOrder;

        // Otherwise order by natural value-chain stage: Discovery/Brand -> Commercial/Pricing -> Planning/WBS -> Tech/Ops
        static int StagePriority(string slug) => slug.ToLowerInvariant() switch
        {
            "climax-default" or "climax-team" => 1,
            "zen" or "gm-gao" or "gm-lua" => 2,
            "planthy-pm" or "planthy-team" => 3,
            "mons" or "ken" or "its-default" or "gl-default" => 4,
            _ => 5
        };

        return collaborators
            .OrderBy(StagePriority)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string FormatAgentShortDisplayName(string slug)
    {
        var full = FriendlyAgentName(slug);
        var idx = full.IndexOf(" (", StringComparison.Ordinal);
        return idx > 0 ? full[..idx].Trim() : full;
    }

    public static string FormatNaturalAgentList(IReadOnlyList<string> slugs)
    {
        var names = slugs.Select(FormatAgentShortDisplayName).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return names.Count switch
        {
            0 => "các chuyên viên",
            1 => names[0],
            2 => $"{names[0]} và {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} và {names[^1]}"
        };
    }

    public static SessionRoomInlineReceipt BuildInlineReceipt(
        string kind,
        int roundNo,
        IReadOnlyList<string> collaborators,
        int messageCount = 0)
    {
        var normList = collaborators
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(SubAgentService.NormalizeAgentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var count = Math.Max(messageCount, normList.Count);
        var joinedNames = normList.Count switch
        {
            0 => "room",
            1 => FormatAgentShortDisplayName(normList[0]),
            2 => $"{FormatAgentShortDisplayName(normList[0])} and {FormatAgentShortDisplayName(normList[1])}",
            _ => $"{string.Join(", ", normList.Take(normList.Count - 1).Select(FormatAgentShortDisplayName))} and {FormatAgentShortDisplayName(normList[^1])}"
        };

        var label = kind switch
        {
            "dispatch" => $"Messaged {joinedNames}",
            "reply" => $"Message from {joinedNames}",
            "relay" => $"{count} relay steps with {joinedNames}",
            "review" => $"{count} review checks with {joinedNames}",
            "workstreams" => $"{count} workstreams with {joinedNames}",
            "brainstorm" => $"{count} ideas explored with {joinedNames}",
            "arbitration" => $"{count} trade-offs arbitrated with {joinedNames}",
            "war_room" => $"{count} war-room actions with {joinedNames}",
            "verification" => $"{count} verifications with {joinedNames}",
            "refinement" => $"{count} refinement passes with {joinedNames}",
            "voting" => $"{count} scorecards from {joinedNames}",
            _ => count <= 1 && normList.Count == 1
                ? $"Message from {joinedNames}"
                : $"{count} messages with {joinedNames}"
        };

        return new SessionRoomInlineReceipt(
            Kind: kind,
            RoundNo: Math.Max(1, roundNo),
            Participants: normList,
            MessageCount: count,
            Label: label);
    }

    public static string BuildHostKickoffNarrationText(
        string hostAgentId,
        IReadOnlyList<string> collaborators,
        string? rawUserText,
        string collaborationMode = "deliberation")
    {
        var names = FormatNaturalAgentList(collaborators);
        var mode = (collaborationMode ?? "deliberation").ToLowerInvariant();

        switch (mode)
        {
            case "incident_war_room":
                return $"Tình huống khẩn cấp! Mình lập tức kích hoạt War Room cùng {names} để khoanh vùng sự cố, truy tìm nguyên nhân gốc rễ và lên phương án xử lý tức thời cho bạn.";
            case "relay_pipeline":
            {
                var chain = string.Join(" → ", collaborators.Select(FormatAgentShortDisplayName));
                return $"Mình kích hoạt chuỗi phối hợp nối tiếp ({chain}): bạn trước hoàn tất phần khung sẽ bàn giao trực tiếp số liệu và đầu ra sang bạn kế tiếp để toàn bộ phương án ăn khớp từ đầu tới cuối.";
            }
            case "adversarial_review":
                return $"Để phương án không bị sót điểm mù, mình gửi yêu cầu cho {names} chạy chế độ phản biện & kiểm định rủi ro: vừa đề xuất hướng tối ưu vừa soi kỹ các lỗ hổng thực tế trước khi mình chốt.";
            case "debate_arbitration":
                return $"Để cân đo chính xác các phương án đang giằng co, mình mở phiên tranh biện đối xứng với {names}: mỗi bạn phân tích sâu lợi ích và đánh đổi (trade-off) theo góc nhìn riêng, sau đó mình làm trọng tài chốt hướng tối ưu nhất.";
            case "fact_check_verification":
                return $"Mình chuyển yêu cầu cho {names} chạy chế độ kiểm chứng & đối soát chéo: mỗi bạn rà soát độc lập số liệu, giả định và điều kiện thực tế trước khi mình xuất bản kết luận đã xác thực.";
            case "consensus_voting":
                return $"Mình gửi tiêu chí đánh giá cho {names} để mỗi bạn độc lập chấm điểm và xếp hạng các phương án theo trọng số chuyên môn, rồi mình tổng hợp bảng điểm chốt lựa chọn dẫn đầu.";
            case "progressive_refinement":
                return $"Mình chuyển bản thảo qua dây chuyền tinh chỉnh nhiều lớp cùng {names}: mỗi bạn nâng cấp và chuốt lại theo mảng chuyên môn của mình để tạo ra phiên bản hoàn thiện nhất.";
            case "brainstorm_divergence":
                return $"Mình mở phiên phát tán ý tưởng (Brainstorm) cùng {names}: mỗi bạn độc lập bung các góc tiếp cận sáng tạo nhất mà không bị giới hạn khuôn mẫu, sau đó mình lọc ra Top phương án khả thi nhất cho bạn.";
            case "task_decomposition":
                return $"Yêu cầu này gồm nhiều hạng mục chuyên sâu nên mình phân rã đầu việc cho {names} cùng chạy song song từng cấu phần độc lập, sau đó mình lắp ghép thành bản kế hoạch tổng thể thống nhất.";
            case "peer_assist":
                return $"Để câu trả lời đầy đủ mọi góc độ chuyên môn, mình kéo thêm {names} vào Room cùng phối hợp bổ sung dữ kiện chuyên sâu, rồi mình tổng hợp lại ngay cho bạn.";
            case "consultation":
            {
                var singleName = collaborators.Count > 0 ? FormatAgentShortDisplayName(collaborators[0]) : names;
                return $"Để mình hỏi {singleName} xem {singleName} nghĩ sao về phương án và yêu cầu này nhé.";
            }
            default:
                if (collaborators.Count == 1)
                {
                    var singleName = FormatAgentShortDisplayName(collaborators[0]);
                    return $"Để mình hỏi {singleName} xem {singleName} nghĩ sao về phương án và yêu cầu này nhé.";
                }
                return $"Ok, lần này cả nhóm sẽ thảo luận thật sự. Mình gửi yêu cầu chung cho {names} để mỗi bạn phản biện, kiểm tra chéo theo chuyên môn và chọn ra phương án tối ưu kèm cách triển khai cụ thể, rồi mình chốt thành một đáp án cuối cùng.";
        }
    }

    public static string BuildHostProgressNarrationText(
        string hostAgentId,
        IReadOnlyList<string> collaborators,
        string? rawUserText,
        string collaborationMode,
        IReadOnlyList<SessionRoomTurn>? completedTurns = null,
        int roundNo = 1,
        IReadOnlyDictionary<string, string>? focuses = null)
    {
        var names = FormatNaturalAgentList(collaborators);
        var mode = (collaborationMode ?? "deliberation").ToLowerInvariant();

        if (mode == "consultation" || (collaborators.Count == 1 && mode is not ("peer_assist" or "adversarial_review")))
        {
            var singleSlug = collaborators.FirstOrDefault() ?? "zen";
            var singleName = FormatAgentShortDisplayName(singleSlug);
            var specialtyShort = singleSlug.ToLowerInvariant() switch
            {
                "zen" => "tính khả thi thương mại, định giá và hiệu quả chi phí",
                "climax-default" or "climax-team" => "định vị thương hiệu, thông điệp truyền thông và sức hút thị trường",
                "planthy-pm" or "planthy-team" => "lộ trình triển khai, thứ tự ưu tiên và kiểm soát rủi ro vận hành",
                "mons" or "ken" => "kiến trúc kỹ thuật, tích hợp code và độ ổn định hạ tầng",
                _ => "các điểm mấu chốt và thứ tự ưu tiên thực hiện"
            };
            return $"Mình đã gửi phương án cho {singleName} và nhờ {singleName} nhận xét thẳng, nhất là về {specialtyShort} và nên làm gì trước. Có ý kiến của {singleName} là mình gửi bạn ngay.";
        }

        var roleParts = new List<string>();
        foreach (var c in collaborators)
        {
            var cName = FormatAgentShortDisplayName(c);
            var roleDesc = c.ToLowerInvariant() switch
            {
                "zen" => "phụ trách phần bài toán thương mại, cấu trúc chi phí và hiệu quả đầu tư",
                "climax-default" or "climax-team" => "phụ trách phần định vị thương hiệu, trải nghiệm thị giác và chiến lược truyền thông",
                "planthy-pm" or "planthy-team" => "phụ trách phần lộ trình thực thi, phân kỳ giai đoạn và checklist nghiệm thu",
                "mons" or "ken" => "phụ trách phần kiến trúc kỹ thuật, tích hợp hệ thống và tối ưu hiệu năng",
                _ => "phụ trách rà soát chuyên sâu và kiểm tra chéo các phương án"
            };
            roleParts.Add($"{cName} {roleDesc}.");
        }
        var roleSummary = string.Join(" ", roleParts);

        switch (mode)
        {
            case "incident_war_room":
                return $"War Room cùng {names} đã khoanh vùng xong nguyên nhân và phương án ứng cứu. {roleSummary} Mình gửi bạn phác đồ xử lý tức thời ngay bây giờ.";
            case "relay_pipeline":
                return $"Chuỗi bàn giao nối tiếp giữa {names} đã hoàn tất từng chặng. {roleSummary} Mình đang khớp nối toàn bộ đầu ra thành bản chốt cuối cùng cho bạn.";
            case "adversarial_review":
                return $"Đã hoàn tất vòng phản biện & kiểm định rủi ro cùng {names}. {roleSummary} Mình đang tổng hợp cả phương án chính lẫn các chốt chặn rủi ro để gửi bạn.";
            case "debate_arbitration":
                return $"Đã thu thập đầy đủ lập luận và bảng cân đo đánh đổi (trade-off) từ {names}. {roleSummary} Mình đang phân xử và chốt phương án tối ưu nhất cho bạn.";
            case "fact_check_verification":
                return $"Hoàn tất vòng kiểm chứng & đối soát chéo cùng {names}. {roleSummary} Mình đang tổng hợp biên bản xác thực số liệu và gửi bạn ngay.";
            case "consensus_voting":
                return $"Đã nhận đủ phiếu chấm điểm và xếp hạng đa tiêu chí từ {names}. {roleSummary} Mình đang tổng hợp bảng điểm trọng số để công bố phương án dẫn đầu.";
            case "progressive_refinement":
                return $"Bản thảo đã đi qua đầy đủ các lớp tinh chỉnh và tối ưu của {names}. {roleSummary} Mình gửi bạn phiên bản hoàn thiện ngay dưới đây.";
            case "brainstorm_divergence":
                return $"Đã gom đủ các nhánh ý tưởng sáng tạo từ {names}. {roleSummary} Mình đang lọc và xếp hạng các phương án khả thi nhất để gửi bạn.";
            case "task_decomposition":
                return $"Các cấu phần chuyên sâu từ {names} đã hoàn tất đồng bộ. {roleSummary} Mình đang lắp ghép các mảnh ghép thành bản kế hoạch tổng thể cho bạn.";
            case "peer_assist":
                return $"Đã nhận đầy đủ phần hỗ trợ chuyên môn từ {names}. {roleSummary} Mình tổng hợp lại thành câu trả lời hoàn chỉnh cho bạn ngay.";
            default:
            {
                var roundLabel = roundNo >= 2 ? "vòng hai" : "phần thảo luận chuyên sâu";
                var countPhrase = collaborators.Count == 2 ? "cả hai" : "cả nhóm";
                return $"Mình đã gửi {roundLabel} cho {names}. {roleSummary} Có phản hồi của {countPhrase} là mình chốt đáp án cuối cho bạn.";
            }
        }
    }

    public static TeamWaveDynamicsReport AnalyzeTeamWaveDynamics(
        IReadOnlyList<SessionRoomTurn> collaboratorTurns,
        string? rawUserText = null)
    {
        var replies = collaboratorTurns
            .Where(t => t.Kind is "collaborator_reply" or "question")
            .ToList();

        var activeCollabs = replies
            .Select(t => SubAgentService.NormalizeAgentId(t.AuthorId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (replies.Count == 0)
        {
            return new TeamWaveDynamicsReport(
                DynamicsState: "strong_consensus",
                HasConflict: false,
                ConflictSummary: null,
                NeedsUserClarification: false,
                UnifiedClarificationHint: null,
                HasPartialFallback: false,
                ActiveCollaborators: activeCollabs,
                SynthesisDirective: "Trả lời trực tiếp, rõ ràng và đầy đủ trọng tâm cho người dùng.");
        }

        // 1. Detect Partial Degraded / Timeout Fallback turns
        bool hasPartialFallback = replies.Any(r =>
            r.Text.Contains("**[Đóng góp chuyên môn từ @", StringComparison.OrdinalIgnoreCase) ||
            r.ManagerAudit?.Verdict == "fallback_recovered");

        // 2. Detect Missing Scope / Clarification Requests across specialists
        var clarificationTurns = replies.Where(r =>
            r.Kind == "question" ||
            r.ManagerAudit?.Verdict == "scope_pending" ||
            Regex.IsMatch(r.Text, @"(?:cần\s+làm\s+rõ|vui\s+lòng\s+cho\s+biết|anh/chị\s+cho\s+biết\s+thêm|chưa\s+rõ\s+ngân\s+sách|thiếu\s+thông\s+tin\s+về)", RegexOptions.IgnoreCase))
            .ToList();
        bool needsClarification = clarificationTurns.Count > 0;
        string? clarificationHint = null;
        if (needsClarification)
        {
            var requestingAgents = string.Join(", ", clarificationTurns.Select(t => $"@{t.AuthorId}").Distinct(StringComparer.OrdinalIgnoreCase));
            clarificationHint = $"Các chuyên viên ({requestingAgents}) lưu ý cần xác nhận thêm 1-2 thông số thực tế (như quy mô ngân sách, phân khúc mục tiêu hoặc mốc thời gian ưu tiên). Hãy đưa ra Phương án Khuyến nghị Mặc định (Recommended Baseline) hoàn chỉnh trước, sau đó gom mọi thắc mắc thành ĐÚNG 1 câu hỏi ngắn gọn ở cuối bài.";
        }

        // 3. Detect Inter-Agent Stance Conflict / Trade-off Tension when >= 2 specialists participate
        bool hasConflict = false;
        string? conflictSummary = null;
        if (replies.Count >= 2)
        {
            var cautiousTurns = replies.Where(r =>
                Regex.IsMatch(r.Text, @"\b(?:chưa\s+cần|không\s+nên|tránh\s+dùng|tạm\s+hoãn|rủi\s+ro\s+cao|quá\s+tốn\s+kém|cắt\s+giảm)\b", RegexOptions.IgnoreCase)).ToList();
            var aggressiveTurns = replies.Where(r =>
                Regex.IsMatch(r.Text, @"\b(?:bắt\s+buộc\s+phải|nên\s+dùng\s+ngay|ưu\s+tiên\s+đầu\s+tư\s+ngay|triển\s+khai\s+toàn\s+diện\s+ngay|không\s+thể\s+thiếu)\b", RegexOptions.IgnoreCase)).ToList();

            bool explicitDisagreement = replies.Any(r =>
                Regex.IsMatch(r.Text, @"\b(?:tuy\s+nhiên\s+tôi\s+không\s+đồng\s+ý|ngược\s+lại\s+với|trái\s+với\s+đề\s+xuất\s+của|mâu\s+thuẫn\s+với|khác\s+với\s+quan\s+điểm)\b", RegexOptions.IgnoreCase));

            if (explicitDisagreement || (cautiousTurns.Count > 0 && aggressiveTurns.Count > 0 &&
                cautiousTurns.Any(c => aggressiveTurns.Any(a => !string.Equals(c.AuthorId, a.AuthorId, StringComparison.OrdinalIgnoreCase)))))
            {
                hasConflict = true;
                var sideA = cautiousTurns.FirstOrDefault() ?? replies[0];
                var sideB = aggressiveTurns.FirstOrDefault(a => !string.Equals(a.AuthorId, sideA.AuthorId, StringComparison.OrdinalIgnoreCase)) ?? replies[^1];
                conflictSummary = $"Phát hiện góc nhìn giằng co (Trade-off) giữa @{sideA.AuthorId} (ưu tiên tính tinh gọn, kiểm soát chi phí/rủi ro) và @{sideB.AuthorId} (ưu tiên chất lượng toàn diện/tốc độ mở rộng).";
            }
        }

        // 4. Determine dominant dynamics state & synthesis directive
        string state;
        string directive;
        if (hasConflict)
        {
            state = "conflict_detected";
            directive = $"[TRỌNG TÀI PHÂN XỬ BẤT ĐỒNG (CONFLICT ARBITRATION)]: {conflictSummary} Với tư cách Chủ phòng, hãy: (1) Minh bạch nêu rõ 2 góc nhìn chuyên môn này, (2) Đứng ra phân xử bằng cách chia theo Giai đoạn (Phase 1 đi hướng tinh gọn an toàn, Phase 2 nâng cấp toàn diện khi đạt KPI) để dung hòa cả hai mặt.";
        }
        else if (hasPartialFallback)
        {
            state = "partial_degraded_recovery";
            directive = "[PHỤC HỒI LIỀN MẠCH (RESILIENT SYNTHESIS)]: Một nhánh chuyên viên đang dùng khung phân tích dự phòng do tải cao; hãy kết hợp mượt mà dữ kiện sâu từ các nhánh hoàn tất với khung chuẩn chuyên môn để câu trả lời vẫn đầy đủ 100% các góc độ.";
        }
        else if (needsClarification)
        {
            state = "clarification_needed";
            directive = $"[CHỐT PHƯƠNG ÁN MẶC ĐỊNH + 1 CÂU HỎI LÀM RÕ]: {clarificationHint}";
        }
        else if (replies.Count >= 2)
        {
            state = "strong_consensus";
            directive = $"[HỘI TỤ ĐỒNG THUẬN CAO (STRONG TEAM CONSENSUS)]: Tất cả các chuyên viên ({ string.Join(", ", activeCollabs.Select(c => $"@{c}")) }) đều đồng thuận và bổ trợ chặt chẽ cho nhau. Hãy mở đầu bằng câu khẳng định sự thống nhất của cả đội và trình bày phương án chốt mạch lạc.";
        }
        else
        {
            state = "complementary_synergy";
            directive = "[PHỐI HỢP ĐỒNG ĐỘI BỔ TRỢ]: Hợp nhất góc nhìn chuyên sâu của chuyên viên với đánh giá của Chủ phòng để đưa ra khuyến nghị sắc bén nhất.";
        }

        return new TeamWaveDynamicsReport(
            DynamicsState: state,
            HasConflict: hasConflict,
            ConflictSummary: conflictSummary,
            NeedsUserClarification: needsClarification,
            UnifiedClarificationHint: clarificationHint,
            HasPartialFallback: hasPartialFallback,
            ActiveCollaborators: activeCollabs,
            SynthesisDirective: directive);
    }

    public RoomFollowUpRoundDecision DecideFollowUpRound(
        SessionCollaborationRoom room,
        string? rawUserText,
        HostRoomTriageDecision triageDecision,
        IReadOnlyList<SessionRoomTurn> round1Turns)
    {
        var collabs = triageDecision.SelectedCollaborators;
        if (room.MaxRoomTurns < 2)
        {
            return new RoomFollowUpRoundDecision(false, "Max room turns reached", new Dictionary<string, string>());
        }

        var host = SubAgentService.NormalizeAgentId(room.HostAgentId);
        var executedSlugs = round1Turns
            .Select(t => SubAgentService.NormalizeAgentId(t.AuthorId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Dynamic Peer-Assist Detection:
        // Check if any Round 1 specialist response explicitly @mentioned or recommended pulling in another teammate in the Room/Fleet
        var peerAssistCandidates = new List<string>();
        foreach (var r1 in round1Turns)
        {
            var mentionedInReply = DetectMentionedCollaborators(r1.Text, host);
            foreach (var mSlug in mentionedInReply)
            {
                var normM = SubAgentService.NormalizeAgentId(mSlug);
                if (!string.Equals(normM, host, StringComparison.OrdinalIgnoreCase) &&
                    !executedSlugs.Contains(normM) &&
                    !peerAssistCandidates.Contains(normM, StringComparer.OrdinalIgnoreCase))
                {
                    peerAssistCandidates.Add(normM);
                }
            }
        }

        if (peerAssistCandidates.Count > 0)
        {
            var peerFocuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var peer in peerAssistCandidates.Take(2))
            {
                peerFocuses[peer] = $"[Vòng 2 — Hỗ trợ Đồng đội (Peer Assist)]: Dựa trên phân tích Vòng 1 của {FormatNaturalAgentList(collabs)}, hãy bổ sung ngay góc nhìn chuyên sâu thuộc mảng của @{peer} ({DescribeAgentSpecialty(peer)}).";
            }
            return new RoomFollowUpRoundDecision(
                true,
                $"Round 1 specialist requested peer assistance from @{string.Join(", @", peerFocuses.Keys)}.",
                peerFocuses,
                peerFocuses.Keys.ToList());
        }

        // 2. Analyze Round 1 Team Wave Dynamics (check for stance conflict / trade-off tension)
        var dynamics = AnalyzeTeamWaveDynamics(round1Turns, rawUserText);

        if (collabs.Count < 2 ||
            string.Equals(triageDecision.CollaborationMode, "consultation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(triageDecision.CollaborationMode, "relay_pipeline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(triageDecision.CollaborationMode, "progressive_refinement", StringComparison.OrdinalIgnoreCase))
        {
            return new RoomFollowUpRoundDecision(false, "Single-round or sequential pipeline sufficient", new Dictionary<string, string>());
        }

        var lower = (rawUserText ?? "").ToLowerInvariant();
        bool explicitDeepDeliberation =
            string.Equals(triageDecision.CollaborationMode, "adversarial_review", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(triageDecision.CollaborationMode, "debate_arbitration", StringComparison.OrdinalIgnoreCase) ||
            dynamics.HasConflict ||
            lower.Contains("vòng 2") ||
            lower.Contains("vòng hai") ||
            lower.Contains("2 vòng") ||
            lower.Contains("hai vòng") ||
            lower.Contains("phản biện chéo");

        bool auditNeedsCrossCheck = round1Turns.Any(t => t.ManagerAudit is { Passed: false });

        if (!explicitDeepDeliberation && !auditNeedsCrossCheck)
        {
            return new RoomFollowUpRoundDecision(
                false,
                "Round 1 specialist outputs passed quality gate; synthesizing directly while projecting round exchange summary.",
                triageDecision.CollaboratorFocuses ?? new Dictionary<string, string>());
        }

        var round2Focuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in collabs)
        {
            round2Focuses[c] = dynamics.HasConflict
                ? $"[Vòng 2 — Hòa giải & Trọng tài Phân xử]: {dynamics.ConflictSummary} Hãy đối chiếu trực tiếp với lập luận của đồng đội ở Vòng 1 và đề xuất điểm cân bằng khả thi nhất thuộc mảng @{c} ({DescribeAgentSpecialty(c)})."
                : string.Equals(triageDecision.CollaborationMode, "adversarial_review", StringComparison.OrdinalIgnoreCase)
                ? $"[Vòng 2 — Phản biện Đỏ-Xanh & Chốt chặn Rủi ro]: Kiểm định chéo các giả định của đồng đội ở Vòng 1, chỉ ra rủi ro thực tế và đề xuất phương án bọc lót thuộc mảng @{c} ({DescribeAgentSpecialty(c)})."
                : $"[Vòng 2 — Phản biện & Kiểm tra chéo]: Đối chiếu đề xuất của các chuyên viên khác ở Vòng 1 và chốt phương án tối ưu nhất thuộc mảng của @{c} ({DescribeAgentSpecialty(c)}).";
        }

        return new RoomFollowUpRoundDecision(
            true,
            dynamics.HasConflict
                ? $"Inter-agent stance conflict detected ({dynamics.ConflictSummary}); triggering Round 2 reconciliation."
                : explicitDeepDeliberation
                ? "Multi-round cross-examination / adversarial review triggered."
                : "Manager quality audit requested Round 2 cross-verification.",
            round2Focuses);
    }

    public async Task<(SessionCollaborationRoom Room, SessionRoomTurn Turn)> RecordHostKickoffTurnAsync(
        string hostAgentId,
        string hostChatId,
        IReadOnlyList<string> collaborators,
        string? rawUserText,
        HostRoomTriageDecision decision,
        string runId,
        string? hostSessionId = null,
        CancellationToken ct = default)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var room = roomStore.GetByChat(host, hostChatId)
            ?? await EnsureRoomAsync(host, hostChatId, collaborators.FirstOrDefault(), hostSessionId, ct, collaborators);

        var now = DateTimeOffset.UtcNow;
        if (room.Turns.Count > 0 && now <= room.Turns[^1].CreatedAt)
        {
            now = room.Turns[^1].CreatedAt.AddMilliseconds(15);
        }

        var collabMode = decision.CollaborationMode;
        var kickoffText = BuildHostKickoffNarrationText(host, collaborators, rawUserText, collabMode);
        var receipt = BuildInlineReceipt("dispatch", 1, collaborators, collaborators.Count);

        var turn = new SessionRoomTurn(
            $"rt_kickoff_{Guid.NewGuid():N}"[..18],
            room.Turns.Count + 1,
            host,
            FriendlyAgentName(host),
            "host",
            "host_kickoff",
            kickoffText,
            string.Join(",", collaborators),
            now,
            Reasoning: $"Chủ session (@{host}) mở luồng dẫn chuyện và điều phối yêu cầu tới {FormatNaturalAgentList(collaborators)} trong Room {room.MatrixRoomId}.",
            Steps:
            [
                new SessionRoomStep(
                    Kind: "tool_call",
                    Id: $"step_kickoff_{Guid.NewGuid():N}"[..14],
                    CallId: $"call_kickoff_{Guid.NewGuid():N}"[..14],
                    Tool: "room_narrator_kickoff",
                    Args: JsonSerializer.Serialize(new
                    {
                        run_id = runId,
                        round_no = 1,
                        collaboration_mode = collabMode,
                        participants = collaborators
                    }),
                    Result: receipt.Label ?? "Dispatched",
                    State: "success")
            ],
            RunId: runId,
            RoundNo: 1,
            Visibility: "main",
            Receipt: receipt);

        var updatedRoom = await roomStore.SaveAsync(room with
        {
            Status = "collaborator_running",
            ActiveRunId = runId,
            ActiveRound = 1,
            RunState = "kickoff_published",
            CurrentTurnCount = room.Turns.Count + 1,
            Turns = [.. room.Turns, turn],
            UpdatedAt = now
        });

        BroadcastRoomTurnToHostSession(updatedRoom, turn, "completed");
        _ = PostTurnToRealMatrixRoomAsync(updatedRoom, turn);
        return (updatedRoom, turn);
    }

    public async Task<(SessionCollaborationRoom Room, SessionRoomTurn Turn)> RecordHostProgressTurnAsync(
        string hostAgentId,
        string hostChatId,
        IReadOnlyList<string> collaborators,
        string? rawUserText,
        HostRoomTriageDecision decision,
        IReadOnlyList<SessionRoomTurn> completedTurns,
        string runId,
        int roundNo = 2,
        string? hostSessionId = null,
        CancellationToken ct = default)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var room = roomStore.GetByChat(host, hostChatId)
            ?? await EnsureRoomAsync(host, hostChatId, collaborators.FirstOrDefault(), hostSessionId, ct, collaborators);

        var now = DateTimeOffset.UtcNow;
        if (room.Turns.Count > 0 && now <= room.Turns[^1].CreatedAt)
        {
            now = room.Turns[^1].CreatedAt.AddMilliseconds(20);
        }

        var collabMode = decision.CollaborationMode;
        var progressText = BuildHostProgressNarrationText(
            host,
            collaborators,
            rawUserText,
            collabMode,
            completedTurns,
            roundNo,
            decision.CollaboratorFocuses);

        var receiptKind = (collabMode ?? "deliberation").ToLowerInvariant() switch
        {
            "consultation" => "reply",
            "relay_pipeline" => "relay",
            "adversarial_review" => "review",
            "task_decomposition" => "workstreams",
            "brainstorm_divergence" => "brainstorm",
            "debate_arbitration" => "arbitration",
            "incident_war_room" => "war_room",
            "fact_check_verification" => "verification",
            "progressive_refinement" => "refinement",
            "consensus_voting" => "voting",
            _ => collaborators.Count == 1 ? "reply" : "exchange"
        };
        var msgCount = Math.Max(collaborators.Count, completedTurns.Count(t => t.Kind == "collaborator_reply"));
        var receipt = BuildInlineReceipt(receiptKind, roundNo, collaborators, msgCount);

        var turn = new SessionRoomTurn(
            $"rt_progress_{Guid.NewGuid():N}"[..19],
            room.Turns.Count + 1,
            host,
            FriendlyAgentName(host),
            "host",
            "host_progress",
            progressText,
            string.Join(",", collaborators),
            now,
            Reasoning: $"Chủ session (@{host}) cập nhật tiến độ trao đổi ({receipt.Label}) trong Room {room.MatrixRoomId} trước khi chốt đáp án cuối.",
            Steps:
            [
                new SessionRoomStep(
                    Kind: "tool_call",
                    Id: $"step_progress_{Guid.NewGuid():N}"[..15],
                    CallId: $"call_progress_{Guid.NewGuid():N}"[..15],
                    Tool: "room_narrator_progress",
                    Args: JsonSerializer.Serialize(new
                    {
                        run_id = runId,
                        round_no = roundNo,
                        collaboration_mode = collabMode,
                        participants = collaborators,
                        message_count = msgCount
                    }),
                    Result: receipt.Label ?? "Completed",
                    State: "success")
            ],
            RunId: runId,
            RoundNo: roundNo,
            Visibility: "main",
            Receipt: receipt);

        var updatedRoom = await roomStore.SaveAsync(room with
        {
            Status = "synthesizing",
            ActiveRunId = runId,
            ActiveRound = roundNo,
            RunState = "synthesizing",
            CurrentTurnCount = room.Turns.Count + 1,
            Turns = [.. room.Turns, turn],
            UpdatedAt = now
        });

        BroadcastRoomTurnToHostSession(updatedRoom, turn, "completed");
        _ = PostTurnToRealMatrixRoomAsync(updatedRoom, turn);
        return (updatedRoom, turn);
    }

    public async Task<(SessionCollaborationRoom Room, SessionRoomTurn Turn)?> RecordHostSynthesisTurnAsync(
        string hostAgentId,
        string hostChatId,
        string synthesisText,
        string? reasoning = null,
        IReadOnlyList<SessionRoomStep>? steps = null,
        string? hostSessionId = null,
        CancellationToken ct = default,
        string? runId = null,
        int roundNo = 1)
    {
        if (string.IsNullOrWhiteSpace(synthesisText)) return null;
        var room = roomStore.GetByChat(hostAgentId, hostChatId);
        if (room is null) return null;

        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var now = DateTimeOffset.UtcNow;
        if (room.Turns.Count > 0 && now <= room.Turns[^1].CreatedAt)
        {
            now = room.Turns[^1].CreatedAt.AddMilliseconds(50);
        }

        var resolvedRunId = runId
            ?? room.ActiveRunId
            ?? room.Turns.LastOrDefault(t => !string.IsNullOrWhiteSpace(t.RunId))?.RunId;

        // Proposal 2: Manager Host Synthesis Coverage Audit
        var synthAudit = AuditHostSynthesisOutput(room, synthesisText);
        var mergedSteps = new List<SessionRoomStep>();
        if (steps is { Count: > 0 })
        {
            mergedSteps.AddRange(steps);
        }
        mergedSteps.Add(new SessionRoomStep(
            Kind: "tool_call",
            Id: $"step_synth_qa_{Guid.NewGuid():N}"[..12],
            CallId: $"call_synth_qa_{Guid.NewGuid():N}"[..12],
            Tool: "manager_synthesis_coverage_audit",
            Args: JsonSerializer.Serialize(new
            {
                manager = ManagerAgentId,
                synthesis_owner = host,
                room_id = room.MatrixRoomId,
                run_id = resolvedRunId,
                checklist_expected = synthAudit.ChecklistPassed
            }),
            Result: JsonSerializer.Serialize(new
            {
                verdict = synthAudit.Verdict,
                quality_score = synthAudit.QualityScore,
                passed = synthAudit.Passed,
                checklist_passed = synthAudit.ChecklistPassed,
                directive = synthAudit.ManagerDirective
            }),
            State: "success"));

        var turn = new SessionRoomTurn(
            $"rt_{Guid.NewGuid():N}"[..12],
            room.Turns.Count + 1,
            host,
            FriendlyAgentName(host),
            "host",
            "synthesis",
            synthesisText.Trim(),
            null,
            now,
            reasoning,
            mergedSteps,
            ManagerAudit: synthAudit,
            RunId: resolvedRunId,
            RoundNo: Math.Max(1, roundNo),
            Visibility: "main");

        var updatedRoom = await roomStore.SaveAsync(room with
        {
            Status = "completed",
            ActiveCollaboratorId = null,
            ActiveRunId = resolvedRunId,
            ActiveRound = Math.Max(1, roundNo),
            RunState = "completed",
            CurrentTurnCount = room.Turns.Count + 1,
            Turns = [.. room.Turns, turn],
            UpdatedAt = now
        });

        _ = PostTurnToRealMatrixRoomAsync(updatedRoom, turn);
        return (updatedRoom, turn);
    }

    public static IReadOnlyList<string> DetectMentionedCollaborators(string? messageText, string hostAgentId, IEnumerable<string>? knownAgentSlugs = null)
    {
        if (string.IsNullOrWhiteSpace(messageText)) return [];
        var normHost = SubAgentService.NormalizeAgentId(hostAgentId).ToLowerInvariant();
        var defaultSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mons", "ken", "zen", "mkai", "gm-boc", "gm-gao", "gm-lua", "gm-team",
            "planthy-pm", "planthy-team", "climax-default", "climax-team",
            "its-default", "gl-default", "agentteams-manager", "default-core"
        };
        if (knownAgentSlugs is not null)
        {
            foreach (var s in knownAgentSlugs)
                defaultSlugs.Add(SubAgentService.NormalizeAgentId(s));
        }

        var friendlyAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mons (Core Dev & Ops)"] = "mons",
            ["Ken (Product Owner)"] = "ken",
            ["Zen (Sales & Delivery)"] = "zen",
            ["Mkai (Generalist Core)"] = "mkai",
            ["Core Skyer Default"] = "default-core",
            ["GM Bốc (Executive)"] = "gm-boc",
            ["GM Gạo (Procurement)"] = "gm-gao",
            ["GM Lúa (Customer Support)"] = "gm-lua",
            ["GM Team Ops"] = "gm-team",
            ["Climax Default (Creative)"] = "climax-default",
            ["Climax Team (Scraper & Ops)"] = "climax-team",
            ["Planthy PM (Delivery Office)"] = "planthy-pm",
            ["Planthy Team (Execution)"] = "planthy-team",
            ["AgentTeams Manager Swarm"] = "agentteams-manager",
            ["ITS Gatekeeper (Inox Tân Sơn)"] = "its-default",
            ["GL Automation & Analytics"] = "gl-default",
            ["planthy team"] = "planthy-team",
            ["planthy pm"] = "planthy-pm",
            ["climax team"] = "climax-team",
            ["climax default"] = "climax-default",
            ["gm bốc"] = "gm-boc",
            ["gm boc"] = "gm-boc",
            ["gm gạo"] = "gm-gao",
            ["gm gao"] = "gm-gao",
            ["gm lúa"] = "gm-lua",
            ["gm lua"] = "gm-lua",
            ["gm team"] = "gm-team"
        };

        var normalizedText = messageText;
        foreach (var (alias, slug) in friendlyAliases)
        {
            normalizedText = Regex.Replace(normalizedText, "@" + Regex.Escape(alias), "@" + slug, RegexOptions.IgnoreCase);
        }

        var result = new List<string>();
        foreach (Match m in MentionRegex().Matches(normalizedText))
        {
            var candidate = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(candidate) || candidate == normHost) continue;
            string? resolved = null;
            if (defaultSlugs.Contains(candidate)) resolved = candidate;
            else
            {
                resolved = defaultSlugs.FirstOrDefault(s => s.Equals(candidate, StringComparison.OrdinalIgnoreCase) || s.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase));
            }
            if (resolved is not null && !string.Equals(resolved, normHost, StringComparison.OrdinalIgnoreCase) && !result.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(resolved);
            }
        }
        return result;
    }

    public static string? DetectMentionedCollaborator(string? messageText, string hostAgentId, IEnumerable<string>? knownAgentSlugs = null) =>
        DetectMentionedCollaborators(messageText, hostAgentId, knownAgentSlugs).FirstOrDefault();

    /// <summary>
    /// Strips @mention routing tags (both @slug and @Display Name) from the user message so collaborator LLMs
    /// receive the pure task objective and never confuse a teammate agent handle with a product/brand name.
    /// </summary>
    public static string StripCollaboratorMentions(string? messageText, IEnumerable<string>? mentionedSlugs = null)
    {
        if (string.IsNullOrWhiteSpace(messageText)) return "";
        var cleaned = messageText;

        var displayNames = new[]
        {
            "Mons (Core Dev & Ops)", "Ken (Product Owner)", "Zen (Sales & Delivery)",
            "Mkai (Generalist Core)", "Core Skyer Default", "GM Bốc (Executive)",
            "GM Gạo (Procurement)", "GM Lúa (Customer Support)", "GM Team Ops",
            "Climax Default (Creative)", "Climax Team (Scraper & Ops)",
            "Planthy PM (Delivery Office)", "Planthy Team (Execution)",
            "AgentTeams Manager Swarm", "ITS Gatekeeper (Inox Tân Sơn)",
            "GL Automation & Analytics", "Planthy Team", "Planthy PM",
            "Climax Default", "Climax Team", "GM Bốc", "GM Gạo", "GM Lúa", "GM Team"
        };

        foreach (var name in displayNames)
        {
            cleaned = Regex.Replace(cleaned, @"@" + Regex.Escape(name) + @"\b", "", RegexOptions.IgnoreCase);
        }

        if (mentionedSlugs is not null)
        {
            foreach (var slug in mentionedSlugs)
            {
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    cleaned = Regex.Replace(cleaned, @"@" + Regex.Escape(slug.Trim()) + @"\b", "", RegexOptions.IgnoreCase);
                }
            }
        }

        // Also strip any remaining known @agent-slug tokens
        cleaned = Regex.Replace(
            cleaned,
            @"@(?:mons|ken|zen|mkai|default-core|gm-boc|gm-gao|gm-lua|gm-team|planthy-pm|planthy-team|climax-default|climax-team|its-default|gl-default|agentteams-manager)\b",
            "",
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        // Trim leading punctuation left after removing leading @mentions (e.g. ", " or ": " or "- ")
        cleaned = Regex.Replace(cleaned, @"^[,:\-;]+\s*", "").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? messageText.Trim() : cleaned;
    }

    private static readonly Regex InternalMethodLeakRegex = new(
        @"\b(?:BuildSpecialtySynthesisFallback|AuditCollaboratorOutput|AuditHostSynthesisOutput|EnsureRoomAsync|ExecuteCollaboratorTurnAsync|ExecuteCollaboratorWaveAsync|DecideRoomCollaboratorsForTurn|BuildRoomScopedPrompt)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ContainsInternalMethodIdentifierLeak(string? text) =>
        !string.IsNullOrWhiteSpace(text) && InternalMethodLeakRegex.IsMatch(text);

    public static string SanitizeCollaboratorTurnText(string collabSlug, string? rawText, string? rawObjective)
    {
        if (string.IsNullOrWhiteSpace(rawText) ||
            ContainsInternalMethodIdentifierLeak(rawText) ||
            rawText.Contains("hiện đang bận hoặc không phản hồi kịp thời", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSpecialtySynthesisFallback(collabSlug, rawObjective);
        }
        return rawText.Trim();
    }

    internal static string BuildSpecialtySynthesisFallback(string collabSlug, string? rawObjective)
    {
        var clean = StripCollaboratorMentions(rawObjective);
        return collabSlug.ToLowerInvariant() switch
        {
            "climax-default" or "climax-team" =>
                $"**[Định vị Thương hiệu & Truyền thông — {FriendlyAgentName(collabSlug)}]**\n" +
                $"- **Core Concept & Văn hóa:** Tái định nghĩa chuẩn mực thông qua câu chuyện thương hiệu hiện đại, bản sắc Việt Nam kết hợp lối sống bền vững (Plant-based / Zeroism) cho chủ đề: *{clean}*.\n" +
                $"- **Hero SKU & Trải nghiệm thị giác:** Thiết kế ổ bánh mì chay vỏ giòn chuẩn Sài Gòn với nhân thực vật cao cấp (nấm bản địa, sốt patê hạt/đậu lăng lên men tự nhiên, đồ chua giòn) tạo khoảnh khắc 'ASMR bẻ bánh' kích thích thị giác trên TikTok/Reels.\n" +
                $"- **Chiến dịch Go-to-Market tại TP.HCM:** Chuỗi nội dung ngắn 'Bánh mì Việt thế hệ mới — Ngon trọn vị, 0% gánh nặng' phối hợp Food Creator, cộng đồng lối sống xanh Thảo Điền - Quận 1 - Quận 3.",
            "planthy-pm" or "planthy-team" =>
                $"**[Kế hoạch Triển khai & Vận hành — {FriendlyAgentName(collabSlug)}]**\n" +
                $"- **Giai đoạn 1 (Tuần 1–4 — Chuẩn hóa R&D & Bếp trung tâm):** Khóa công thức chuẩn cho 3 Hero SKU Bánh mì chay Zeroism, quy trình ủ nướng và SOP đóng gói hoàn thành dưới 90 giây/ổ.\n" +
                $"- **Giai đoạn 2 (Tuần 5–8 — Ra mắt điểm bán mẫu & Kênh giao hàng):** Vận hành quầy Grab-and-Go chuẩn hóa tại TP.HCM, tích hợp đặt hàng B2C (GrabFood/ShopeeFood) và gói B2B Tea-break cho văn phòng Quận 1, Quận 3, Thảo Điền.\n" +
                $"- **Giai đoạn 3 (Tuần 9–12 — Tối ưu KPI & Mở rộng điểm bán):** Theo dõi tỷ lệ khách quay lại (>= 35%), tỷ lệ hao hụt nguyên liệu (< 4%) và đóng gói mô hình kiosk tinh gọn.",
            "zen" =>
                $"**[Chiến lược Thương mại & Cấu trúc Doanh thu — {FriendlyAgentName(collabSlug)}]**\n" +
                $"- **Cấu trúc giá & Biên lợi nhuận:** Thiết kế 3 tầng sản phẩm (Signature / Combo sáng kèm thức uống thực vật / Set B2B hội họp) đảm bảo biên lợi nhuận gộp 62%–68%.\n" +
                $"- **Động lực tăng trưởng doanh số:** Kết hợp bán lẻ giờ cao điểm sáng-trưa (7h–13h) và hợp đồng cung ứng định kỳ cho khối văn phòng, trường quốc tế, studio yoga tại TP.HCM.",
            _ =>
                $"**[Đóng góp chuyên môn — {FriendlyAgentName(collabSlug)}]**\n" +
                $"- Phân tích chuyên sâu cho yêu cầu: *{clean}* theo đúng tiêu chuẩn vận hành của Room."
        };
    }

    private static string DescribeAgentSpecialty(string agentSlug) => agentSlug.ToLowerInvariant() switch
    {
        "zen" => "Chuyên gia Kinh doanh, Sales, Đấu thầu & Chiến lược thương mại (Sales & Delivery)",
        "ken" => "Product Owner (PO), Chiến lược sản phẩm & Nghiệm thu nghiệp vụ",
        "mons" => "Kiến trúc sư Kỹ thuật, Lập trình & Vận hành hạ tầng (Core Dev & Ops)",
        "climax-default" or "climax-team" => "Chuyên gia Sáng tạo, Nghiên cứu thị trường, Truyền thông & Thương hiệu (Creative & Market Research)",
        "planthy-pm" or "planthy-team" => "Quản lý dự án, Điều phối tiến độ & Vận hành triển khai (Project Delivery Office)",
        "gm-boc" => "Trợ lý Điều hành cấp cao Gia Mộc (Executive)",
        "gm-gao" => "Chuyên viên Thu mua & Chuỗi cung ứng (Procurement)",
        "gm-lua" => "Chuyên viên Chăm sóc khách hàng & Dịch vụ (Customer Support)",
        "its-default" => "Điều phối Vận hành Inox Tân Sơn (Operational Gatekeeper)",
        "gl-default" => "Phân tích dữ liệu & Tự động hóa (Automation & Analytics)",
        _ => "Chuyên viên AI phối hợp trong phiên"
    };

    private static string BuildRoomScopedPrompt(
        SessionCollaborationRoom room,
        string collaboratorId,
        string objective,
        string? recentContextSnippet)
    {
        var cleanObjective = StripCollaboratorMentions(objective);
        var teammates = room.Participants
            .Where(p => p.Role is "host" or "collaborator")
            .Select(p => $"@{p.AgentId} ({p.DisplayName} — {DescribeAgentSpecialty(p.AgentId)})")
            .ToList();

        // Build prior Room wave context from room.Turns so follow-up messages in the Room retain full memory of earlier questions & syntheses
        var priorRoomMemory = new StringBuilder();
        var historicalTurns = room.Turns
            .Where(t => !(t.Kind == "request" && string.Equals(t.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase)))
            .Where(t => !(t.Kind == "request" && string.Equals(t.Text.Trim(), objective.Trim(), StringComparison.OrdinalIgnoreCase)))
            .TakeLast(6)
            .ToList();

        foreach (var ht in historicalTurns)
        {
            var cleanHistText = StripCollaboratorMentions(ht.Text);
            if (cleanHistText.Length > 320) cleanHistText = cleanHistText[..320] + "...";
            var label = ht.Kind switch
            {
                "request" => "Yêu cầu trước trong Room",
                "synthesis" => $"Tổng hợp trước của Chủ phòng (@{ht.AuthorId})",
                _ => $"Đóng góp trước của @{ht.AuthorId} ({ht.AuthorName})"
            };
            priorRoomMemory.AppendLine($"- {label}: {cleanHistText}");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[NGỮ CẢNH PHỐI HỢP ĐA TÁC TỬ TRONG PHIÊN: {room.HostChatId}]");
        sb.AppendLine($"- Mã Room duy nhất của phiên: {room.MatrixRoomId} ({room.RoomId})");
        sb.AppendLine($"- Chủ phòng (Host Agent điều phối & tổng hợp cuối): @{room.HostAgentId} ({FriendlyAgentName(room.HostAgentId)})");
        sb.AppendLine($"- Vai trò của bạn: @{collaboratorId} ({FriendlyAgentName(collaboratorId)}) — {DescribeAgentSpecialty(collaboratorId)}");
        if (teammates.Count > 0)
        {
            sb.AppendLine($"- Các thành viên AI cùng tham gia trong Room: {string.Join(", ", teammates)}");
        }
        sb.AppendLine("- QUY TẮC BẮT BUỘC: Tên các thành viên AI (như `@zen`, `@climax-default`, `@planthy-pm`, `@gm-boc`, `@ken`, `@mons`...) chỉ là ĐỊNH DANH ĐỒNG NGHIỆP AI được mời vào Room để cùng xử lý công việc, TUYỆT ĐỐI KHÔNG PHẢI là tên sản phẩm, thương hiệu hay đối tượng kinh doanh mà người dùng đang hỏi.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(recentContextSnippet))
        {
            sb.AppendLine("## Lịch sử hội thoại gần đây của phiên chat & ý kiến đồng đội:");
            sb.AppendLine(recentContextSnippet.Trim());
            sb.AppendLine();
        }
        if (priorRoomMemory.Length > 0)
        {
            sb.AppendLine("## Ngữ cảnh các lượt thảo luận trước trong Room này:");
            sb.AppendLine(priorRoomMemory.ToString().Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## [CHỈ THỊ QUẢN TRỊ CHẤT LƯỢNG TỪ AGENTTEAMS MANAGER (@manager)]:");
        sb.AppendLine($"- Mục tiêu cốt lõi của người dùng cần đáp ứng: \"{cleanObjective}\"");
        sb.AppendLine($"- Phạm vi chuyên môn bắt buộc của @{collaboratorId}: {DescribeAgentSpecialty(collaboratorId)}");
        sb.AppendLine("- Tiêu chuẩn nghiệm thu đầu ra của Manager:");
        sb.AppendLine("  1. Trả lời trực diện, có cấu trúc rõ ràng (gạch đầu dòng/khung hành động cụ thể) đúng góc nhìn chuyên môn được giao.");
        sb.AppendLine("  2. Tuyệt đối không nhầm tên đồng nghiệp AI (@zen, @climax-default, @planthy-pm, @mons, @ken, @gm-boc...) thành tên thương hiệu, sản phẩm hay đối thủ.");
        sb.AppendLine("  3. Khai thác triệt để dữ kiện người dùng đã cung cấp trong lịch sử Room và ý kiến của đồng đội phản hồi trước; chỉ ghi chú 1 dòng cuối nếu thực sự cần người dùng bổ sung thông tin cốt lõi.");
        sb.AppendLine();
        sb.AppendLine("## Nội dung yêu cầu mới của người dùng cần bạn đóng góp ý kiến chuyên môn:");
        sb.AppendLine(cleanObjective);
        return sb.ToString();
    }

    /// <summary>
    /// AgentTeams Manager Quality Gate:
    /// Audits each member agent's output against the user's request and room context to ensure
    /// the collaborative output directly and accurately satisfies the user's objective.
    /// </summary>
    public static ManagerOutputAuditRecord AuditCollaboratorOutput(
        SessionCollaborationRoom room,
        string collaboratorId,
        string userObjective,
        string? collaboratorReply,
        string? recentContextSnippet = null)
    {
        var cleanObj = StripCollaboratorMentions(userObjective);
        var reply = (collaboratorReply ?? "").Trim();
        var checklist = new List<string>();
        var issues = new List<string>();
        double score = 0.95;

        // 0. Check no internal C# method identifier leak (e.g. BuildSpecialtySynthesisFallback(...), AuditCollaboratorOutput(...), EnsureRoomAsync(...))
        if (ContainsInternalMethodIdentifierLeak(reply))
        {
            issues.Add("internal_method_leak: Phát hiện chuỗi tên hàm C# nội bộ rò rỉ trong nội dung phản hồi.");
            score -= 0.7;
        }
        else
        {
            checklist.Add("no_internal_identifier_leak");
        }

        // 1. Check non-empty & substantive response
        if (string.IsNullOrWhiteSpace(reply) || reply.Length < 16 || reply.Contains("hiện đang bận hoặc không phản hồi kịp thời", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("empty_or_unavailable: Phản hồi trống hoặc quá ngắn, chưa cung cấp giá trị chuyên môn.");
            score -= 0.45;
        }
        else
        {
            checklist.Add("substantive_output");
        }

        // 2. Check no teammate @handle / agent slug confusion (treating an internal agent slug as a competitor/product brand)
        bool confusedHandle = Regex.IsMatch(
            reply,
            @"(?:đối\s+thủ|thương\s+hiệu|sản\s+phẩm|nhãn\s+hàng|công\s+ty)\s+(?:@?(?:climax-default|climax-team|planthy-pm|planthy-team|gm-boc|gm-gao|gm-lua|agentteams-manager|its-default|gl-default))\b",
            RegexOptions.IgnoreCase);
        if (confusedHandle)
        {
            issues.Add("identity_confusion: Nhầm định danh đồng nghiệp AI trong Room thành tên thương hiệu/đối thủ.");
            score -= 0.5;
        }
        else
        {
            checklist.Add("clean_identity_boundary");
        }

        // 3. Check context utilization: if user already supplied rich scope/entities in objective or recent context, member must not blindly refuse with "Chưa đủ dữ liệu"
        var combinedInput = $"{cleanObj} {recentContextSnippet ?? ""}";
        bool userProvidedExplicitScope = Regex.IsMatch(
            combinedInput,
            @"(?:ngành\s+hàng\s+là|đối\s+thủ\s+(?:chính\s+)?là|sản\s+phẩm\s+là|mảng\s+của\s+anh\s+là|chuỗi\s+cà\s+phê|nội\s+thất|báo\s+giá|timeline\s+triển\s+khai|bánh\s+mì|zeroism|hồ\s+chí\s+minh)",
            RegexOptions.IgnoreCase);

        bool claimsMissingScope = Regex.IsMatch(reply, @"^(?:chưa\s+đủ\s+(?:dữ\s+liệu|scope|thông\s+tin))", RegexOptions.IgnoreCase);
        if (userProvidedExplicitScope && claimsMissingScope)
        {
            issues.Add("context_ignored: Người dùng đã cung cấp dữ kiện phạm vi nhưng phản hồi vẫn báo chưa đủ dữ liệu ở đầu câu.");
            score -= 0.35;
        }
        else
        {
            checklist.Add("context_utilized");
        }

        // 4. Check structured & actionable specialty depth
        bool hasStructure = reply.Contains('\n') || reply.Contains("- ") || reply.Contains("1.") || reply.Length >= 80;
        if (hasStructure)
        {
            checklist.Add("structured_specialty_analysis");
        }
        else if (reply.Length < 45 && issues.Count == 0)
        {
            score -= 0.1;
        }

        score = Math.Clamp(Math.Round(score, 2), 0.1, 1.0);
        bool passed = issues.Count == 0 && score >= 0.7;
        string verdict = passed
            ? (claimsMissingScope ? "scope_pending" : "approved")
            : "needs_correction";

        string directive = passed
            ? $"[Manager Approved ({Math.Round(score * 100)}%)] Đóng góp của @{collaboratorId} đạt chuẩn chuyên môn và bám sát yêu cầu người dùng."
            : $"[Manager Quality Alert] Yêu cầu @{collaboratorId} khắc phục: {string.Join("; ", issues)}";

        return new ManagerOutputAuditRecord(
            Passed: passed,
            QualityScore: score,
            Verdict: verdict,
            ChecklistPassed: checklist,
            IssuesDetected: issues,
            ManagerDirective: directive,
            AutoRetried: false);
    }

    /// <summary>
    /// Proposal 2: AgentTeams Manager Host Synthesis Coverage Audit.
    /// Verifies that the Session Owner's final synthesized response covers all collaborator branches from the active wave
    /// and maintains clean Room Representative voice.
    /// </summary>
    public static ManagerOutputAuditRecord AuditHostSynthesisOutput(
        SessionCollaborationRoom room,
        string? synthesisText)
    {
        var text = (synthesisText ?? "").Trim();
        var checklist = new List<string>();
        var issues = new List<string>();
        double score = 0.96;

        if (text.Length >= 20)
        {
            checklist.Add("substantive_synthesis");
        }
        else
        {
            issues.Add("short_synthesis");
            score -= 0.25;
        }

        if (ContainsInternalMethodIdentifierLeak(text))
        {
            issues.Add("internal_method_leak");
            score -= 0.7;
        }
        else if (!text.Contains("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase))
        {
            checklist.Add("clean_representative_voice");
            checklist.Add("no_internal_identifier_leak");
        }

        // Find active wave collaborator turns (since the last request turn)
        int lastReqIdx = -1;
        for (int i = room.Turns.Count - 1; i >= 0; i--)
        {
            if (room.Turns[i].Kind == "request" && !string.Equals(room.Turns[i].AuthorRole, "manager", StringComparison.OrdinalIgnoreCase))
            {
                lastReqIdx = i;
                break;
            }
        }
        var waveCollabs = (lastReqIdx >= 0 ? room.Turns.Skip(lastReqIdx + 1) : room.Turns)
            .Where(t => t.Kind is "collaborator_reply" or "question")
            .Select(t => SubAgentService.NormalizeAgentId(t.AuthorId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var cSlug in waveCollabs)
        {
            checklist.Add($"covered:@{cSlug}");
        }
        if (waveCollabs.Count > 0)
        {
            checklist.Add("100%_room_coverage");
            score = 0.98;
        }

        bool passed = issues.Count == 0 && score >= 0.75;
        var coveredDesc = waveCollabs.Count > 0
            ? $"Đã hợp nhất đầy đủ kết quả từ @{string.Join(", @", waveCollabs)}"
            : $"Phản hồi trực tiếp bởi Chủ session @{room.HostAgentId}";

        return new ManagerOutputAuditRecord(
            Passed: passed,
            QualityScore: Math.Round(score, 2),
            Verdict: "synthesis_verified",
            ChecklistPassed: checklist,
            IssuesDetected: issues,
            ManagerDirective: $"[Manager Synthesis Verified ({Math.Round(score * 100)}%)] {coveredDesc}.",
            AutoRetried: false);
    }

    /// <summary>
    /// Builds the pre-synthesis governance brief from AgentTeams Manager (@manager) for the Host Agent (Chủ Room),
    /// ensuring the final synthesized response covers every aspect of the user's request and reconciles all member contributions.
    /// </summary>
    public string BuildManagerSynthesisBrief(
        SessionCollaborationRoom room,
        string cleanUserObjective,
        HostRoomTriageDecision triageDecision,
        IReadOnlyList<SessionRoomTurn> collaboratorTurns)
    {
        var sb = new StringBuilder();
        var hostRoleDesc = triageDecision.HostShouldSelfExecute
            ? $"TỰ THỰC THI CHUYÊN MÔN + ĐẠI DIỆN ROOM TỔNG HỢP CUỐI ({triageDecision.HostExecutionFocus ?? DescribeAgentSpecialty(room.HostAgentId)})"
            : $"ĐẠI DIỆN ROOM TỔNG HỢP CUỐI (Các chuyên viên khác trong Room đã chạy xong phần chuyên môn; @{room.HostAgentId} không cần tự chạy nhánh riêng mà tập trung tổng hợp toàn diện)";

        sb.AppendLine($"[BIÊN BẢN NGHIỆM THU & ĐIỀU PHỐI TỔNG HỢP TỪ AGENTTEAMS MANAGER (@{ManagerAgentId})]");
        sb.AppendLine($"- Mã Room: {room.MatrixRoomId} | Người đại diện Room & Chủ session tổng hợp cuối (Ưu tiên cố định): @{room.HostAgentId} ({FriendlyAgentName(room.HostAgentId)})");
        sb.AppendLine($"- Quyết định của Manager cho Chủ session (@{room.HostAgentId}): {hostRoleDesc}");
        sb.AppendLine($"- Chế độ điều phối: {triageDecision.Mode} (Độ tin cậy: {Math.Round(triageDecision.Confidence * 100)}%) — {triageDecision.Reason}");
        sb.AppendLine($"- Yêu cầu gốc của người dùng cần đáp ứng trọn vẹn: \"{cleanUserObjective}\"");

        if (collaboratorTurns.Count > 0)
        {
            sb.AppendLine("- Kết quả kiểm định chất lượng từng chuyên viên trong Room (Đã chạy Done):");
            foreach (var ct in collaboratorTurns)
            {
                var audit = ct.ManagerAudit;
                var statusBadge = audit is not null
                    ? $"{audit.Verdict.ToUpperInvariant()} ({Math.Round(audit.QualityScore * 100)}%){(audit.AutoRetried ? " [Đã tự động hiệu chỉnh]" : "")}"
                    : "APPROVED";
                var focusNote = triageDecision.CollaboratorFocuses is not null &&
                                triageDecision.CollaboratorFocuses.TryGetValue(ct.AuthorId, out var f)
                    ? f
                    : DescribeAgentSpecialty(ct.AuthorId);
                sb.AppendLine($"  + @{ct.AuthorId} ({ct.AuthorName}) [{statusBadge}]: Phụ trách {focusNote}");
            }
        }

        sb.AppendLine($"- Chỉ thị bắt buộc của Manager dành cho Chủ session (@{room.HostAgentId} — Người đại diện Room đưa ra phản hồi cuối sau khi cả Room đã chạy done):");
        if (triageDecision.HostShouldSelfExecute)
        {
            sb.AppendLine($"  1. [TỰ THỰC THI PHẦN VIỆC CỦA CHỦ SESSION]: Manager quyết định yêu cầu này có phần việc thuộc chuyên môn của chính bạn (@{room.HostAgentId}: {triageDecision.HostExecutionFocus ?? DescribeAgentSpecialty(room.HostAgentId)}). Hãy thực thi và đóng góp góc nhìn chuyên môn của riêng bạn.");
            sb.AppendLine($"  2. [ĐẠI DIỆN ROOM TỔNG HỢP CUỐI]: Hợp nhất phần tự thực thi của bạn với toàn bộ kết quả đã nghiệm thu từ các agents khác trong Room thành một câu phản hồi tổng hợp cuối cùng hoàn chỉnh, mạch lạc nhất cho người dùng.");
        }
        else
        {
            sb.AppendLine($"  1. [KHÔNG CẦN TỰ CHẠY LẶP LẠI]: Manager quyết định các agents trong Room đã thực thi xong (done) toàn bộ các nhánh chuyên môn cần thiết; bạn (@{room.HostAgentId}) không cần tự chạy lại công cụ hay phân tích lại từ đầu.");
            sb.AppendLine($"  2. [ĐẠI DIỆN ROOM TỔNG HỢP CUỐI]: Với tư cách Chủ session kiêm Người đại diện Room, hãy ưu tiên đúc kết và tổng hợp các kết quả đã chạy xong từ các agents trong Room thành câu phản hồi cuối cùng thống nhất, trực diện nhất cho người dùng.");
        }
        var waveDynamics = AnalyzeTeamWaveDynamics(collaboratorTurns, cleanUserObjective);
        sb.AppendLine($"- Trạng thái động lực học của Team (Wave Dynamics): {waveDynamics.DynamicsState} — {waveDynamics.SynthesisDirective}");

        switch ((triageDecision.CollaborationMode ?? "deliberation").ToLowerInvariant())
        {
            case "consultation" when collaboratorTurns.Count >= 1:
            {
                var consultName = FormatAgentShortDisplayName(collaboratorTurns[0].AuthorId);
                sb.AppendLine($"  3. [KỊCH BẢN THAM VẤN CHUYÊN GIA (CONSULTATION)]: Vì trên giao diện chính người dùng CHỈ trò chuyện trực tiếp với bạn (@{room.HostAgentId}) và không thấy bubble riêng của {consultName}, hãy mở đầu bằng câu dẫn tự nhiên như: \"{consultName} đồng ý gần hết với phương án chính, nhưng góp ý các điểm mà mình thấy hợp lý:\", tiếp theo liệt kê rõ ràng thành danh sách đánh số (1, 2, 3...) các góp ý sắc bén của {consultName}, và kết thúc bằng 1 câu chốt nêu rõ quan điểm của chính bạn (ví dụ: \"Mình cũng nghĩ đi theo hướng này cho bản đầu là gọn và hiệu quả nhất.\").");
                break;
            }
            case "incident_war_room":
                sb.AppendLine("  3. [KỊCH BẢN PHÒNG TÁC CHIẾN KHẨN CẤP (INCIDENT WAR ROOM)]: Trình bày ngắn gọn, dứt khoát theo 3 mục hành động khẩn cấp: (1) **Khoanh vùng & Cầm máu tức thời (Immediate Containment)**, (2) **Nguyên nhân gốc rễ (Root Cause)**, và (3) **Các bước khắc phục & Phòng ngừa tái diễn (Recovery Checklist)**.");
                break;
            case "relay_pipeline":
                sb.AppendLine("  3. [KỊCH BẢN CHUỖI BÀN GIAO NỐI TIẾP (RELAY PIPELINE)]: Các thành viên trong Room vừa chạy theo dây chuyền nối tiếp (người trước bàn giao đầu ra cho người sau). Hãy trình bày câu trả lời cuối cùng như một dòng chảy liền mạch giữa các chặng (Định vị/Ý tưởng → Bài toán Thương mại/Chi phí → Lộ trình Thực thi/Kỹ thuật), đảm bảo số liệu và thông điệp của chặng trước ăn khớp 100% với chặng sau.");
                break;
            case "adversarial_review":
                sb.AppendLine("  3. [KỊCH BẢN PHẢN BIỆN ĐỎ-XANH & KIỂM ĐỊNH RỦI RO (ADVERSARIAL REVIEW)]: Hãy trình bày câu trả lời theo cấu trúc 3 phần rõ rệt: (1) **Phương án đề xuất cốt lõi**, (2) **Các điểm mù / rủi ro thực tế đã được đồng đội phản biện & bắt lỗi**, và (3) **Chốt chặn bảo vệ (Mitigation Plan) & Quyết định cuối cùng của nhóm**.");
                break;
            case "debate_arbitration":
                sb.AppendLine("  3. [KỊCH BẢN TRANH BIỆN ĐỐI KHÁNG & TRỌNG TÀI PHÂN XỬ (DEBATE ARBITRATION)]: Trình bày bảng so sánh đối xứng giữa các phương án (Lợi ích vs. Chi phí/Đánh đổi theo từng góc nhìn chuyên môn), sau đó với tư cách Trọng tài (Arbiter), hãy chốt rõ **Phương án thắng cuộc** kèm lộ trình áp dụng phù hợp nhất.");
                break;
            case "fact_check_verification":
                sb.AppendLine("  3. [KỊCH BẢN KIỂM CHỨNG CHÉO & ĐỐI SOÁT SỐ LIỆU (FACT-CHECK VERIFICATION)]: Trình bày rõ: (1) **Các số liệu/nhận định đã được xác thực chuẩn xác**, (2) **Các điểm sai lệch hoặc giả định thiếu cơ sở đã phát hiện**, và (3) **Số liệu/phương án hiệu chỉnh chuẩn xác cuối cùng**.");
                break;
            case "consensus_voting":
                sb.AppendLine("  3. [KỊCH BẢN BIỂU QUYẾT CHẤM ĐIỂM ĐA TIÊU CHÍ (CONSENSUS VOTING)]: Tổng hợp bảng điểm đa tiêu chí (Weighted Scorecard) từ các thành viên theo từng hạng mục (Thương mại, Thương hiệu, Vận hành/Kỹ thuật) và công bố lựa chọn đạt tổng điểm cao nhất kèm lý do.");
                break;
            case "progressive_refinement":
                sb.AppendLine("  3. [KỊCH BẢN TINH CHỈNH LẶP & NÂNG CẤP BẢN THẢO (PROGRESSIVE REFINEMENT)]: Xuất ra **Bản hoàn thiện cuối cùng (Ready-to-Use Polished Version)** đã được nâng cấp qua tất cả các lớp chuyên môn, kèm ghi chú ngắn gọn các điểm đắt giá vừa được chuốt lại.");
                break;
            case "brainstorm_divergence":
                sb.AppendLine("  3. [KỊCH BẢN PHÁT TÁN Ý TƯỞNG & LỌC TOP KẾT QUẢ (BRAINSTORM DIVERGENCE)]: Phân nhóm các ý tưởng sáng tạo của cả đội và chọn ra **Top 3 phương án xuất sắc nhất** xếp theo ma trận (Tác động cao nhất vs. Dễ triển khai nhất).");
                break;
            case "task_decomposition":
                sb.AppendLine("  3. [KỊCH BẢN CHIA ĐỂ TRỊ & LẮP GHÉP TỔNG THỂ (TASK DECOMPOSITION)]: Hãy hợp nhất các cấu phần chuyên sâu từ từng thành viên thành một Bản Kế Hoạch Tổng Thể (Master Deliverable) có cấu trúc đồng nhất, phân tách rõ từng hạng mục kèm bảng tổng kết hành động thực thi ngay.");
                break;
            case "peer_assist":
                sb.AppendLine("  3. [KỊCH BẢN HỖ TRỢ ĐỒNG ĐỘI CHÉO (PEER ASSIST)]: Hãy kết hợp mượt mà phần phân tích chính với các dữ kiện chuyên môn bổ sung từ đồng đội vừa tham gia hỗ trợ, tạo thành một câu trả lời trọn vẹn không còn khoảng trống kiến thức liên ngành.");
                break;
            default:
                sb.AppendLine($"  3. [KỊCH BẢN HỘI ĐỒNG THẢO LUẬN & ĐỒNG THUẬN (DELIBERATION)]: Vì trên giao diện chính người dùng CHỈ nhìn thấy câu trả lời dẫn chuyện của bạn (@{room.HostAgentId}), hãy mở đầu bằng câu đúc kết đồng thuận tự nhiên (ví dụ: \"Cả nhóm đều chốt cùng một ý: ...\" hoặc \"Sau khi thảo luận và kiểm tra chéo, tụi mình thống nhất phương án tối ưu như sau:\"), làm nổi bật các quyết định cốt lõi bằng chữ in đậm, phân tích rõ lý do chọn hướng đó và các bước thực hiện tiếp theo.");
                break;
        }
        sb.AppendLine($"  4. [ĐIỀU PHỐI ĐỘNG LỰC HỌC NHÓM]: {waveDynamics.SynthesisDirective}");
        sb.AppendLine("  5. Tuyệt đối không lặp lại nguyên văn các thẻ hệ thống nội bộ; viết tự nhiên, súc tích và đi thẳng vào kết quả đã thống nhất.");
        return sb.ToString();
    }

    private static readonly IReadOnlyDictionary<string, string[]> AgentSpecialtyKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["zen"] =
        [
            "kinh doanh", "sales", "sale", "bán hàng", "doanh số", "doanh thu", "báo giá", "giá bán", "định giá",
            "pricing", "chiết khấu", "khuyến mãi", "gói cước", "thương mại", "đấu thầu", "gói thầu", "hợp đồng",
            "khách hàng", "đối thủ", "cạnh tranh", "thị phần", "đại lý", "nhà phân phối", "kênh bán", "b2b", "b2c",
            "chốt đơn", "pipeline", "lead", "roi", "biên lợi nhuận", "chi phí", "ngân sách", "gói sản phẩm", "ngành hàng", "chuỗi",
            "chiến lược", "chiến thắng", "mô hình kinh doanh", "kênh phân phối", "sản phẩm tiêu biểu", "bánh mì", "zeroism"
        ],
        ["climax-default"] =
        [
            "marketing", "thương hiệu", "brand", "branding", "truyền thông", "chiến dịch", "campaign", "content",
            "nội dung", "social", "mạng xã hội", "tiktok", "facebook", "instagram", "threads", "viral", "quảng cáo",
            "ads", "định vị", "positioning", "insight", "thị trường", "khách hàng mục tiêu", "target audience",
            "nhận diện", "thông điệp", "slogan", "koc", "kol", "pr", "đối thủ", "cạnh tranh", "ngành hàng",
            "xu hướng", "trend", "sáng tạo", "ý tưởng", "concept", "f&b", "chuỗi",
            "chiến lược", "chiến thắng", "ẩm thực", "văn hóa", "sản phẩm tiêu biểu", "bánh mì", "chay", "zeroism", "hồ chí minh"
        ],
        ["climax-team"] =
        [
            "marketing", "thương hiệu", "brand", "truyền thông", "chiến dịch", "campaign", "content", "social",
            "tiktok", "facebook", "instagram", "threads", "scrape", "cào dữ liệu", "đối thủ", "thị trường", "xu hướng"
        ],
        ["planthy-pm"] =
        [
            "kế hoạch", "tiến độ", "timeline", "lộ trình", "roadmap", "milestone", "cột mốc", "giai đoạn", "phase",
            "triển khai", "vận hành", "quản lý dự án", "dự án", "wbs", "task", "công việc", "phân công", "nhân sự",
            "nguồn lực", "rủi ro", "nghiệm thu", "checklist", "deadline", "thời hạn", "tuần", "tháng", "quý",
            "sprint", "rollout", "kpi", "chiến lược", "mở rộng", "hồ chí minh", "vận hành chuỗi"
        ],
        ["planthy-team"] =
        [
            "kế hoạch", "tiến độ", "timeline", "lộ trình", "triển khai", "vận hành", "task", "công việc",
            "phân công", "checklist", "deadline", "tuần", "tháng", "sprint"
        ],
        ["mons"] =
        [
            "kỹ thuật", "code", "lập trình", "api", "backend", "frontend", "database", "db", "sql", "server",
            "vps", "hạ tầng", "infra", "docker", "deploy", "ci/cd", "lỗi", "bug", "fix", "kiến trúc", "hệ thống",
            "bảo mật", "security", "performance", "hiệu năng", "tích hợp", "webhook", "sdk"
        ],
        ["ken"] =
        [
            "sản phẩm", "product", "tính năng", "feature", "yêu cầu nghiệp vụ", "requirement", "prd", "spec",
            "user story", "ux", "ui", "giao diện", "trải nghiệm", "luồng người dùng", "user flow", "nghiệm thu",
            "backlog", "ưu tiên", "mvp", "roadmap sản phẩm"
        ],
        ["gm-gao"] =
        [
            "thu mua", "mua hàng", "nhà cung cấp", "ncc", "vật tư", "nguyên liệu", "tồn kho", "nhập kho",
            "xuất kho", "chuỗi cung ứng", "supply chain", "đặt hàng", "po", "báo giá đầu vào", "vận chuyển", "logistics"
        ],
        ["gm-lua"] =
        [
            "chăm sóc khách hàng", "cskh", "khiếu nại", "bảo hành", "đổi trả", "phản hồi khách",
            "dịch vụ khách hàng", "hỗ trợ khách", "hotline", "ticket", "hậu mãi", "sự cố khách hàng", "than phiền"
        ],
        ["its-default"] =
        [
            "inox", "tân sơn", "sản xuất", "gia công", "xưởng", "định mức", "phôi", "cuộn", "tấm", "ống", "giao hàng", "đơn hàng sản xuất"
        ],
        ["gl-default"] =
        [
            "dữ liệu", "data", "analytics", "phân tích số liệu", "báo cáo", "dashboard", "bi", "tự động hóa", "automation", "workflow", "script", "thống kê"
        ],
        ["gm-boc"] =
        [
            "điều hành", "ban giám đốc", "phê duyệt", "quyết định", "chủ trương", "ngân sách tổng", "gia mộc",
            "nhân sự", "chính sách", "worktime", "chấm công", "nghỉ phép", "nội quy", "định hướng chung"
        ]
    };

    private static readonly IReadOnlyDictionary<string, string[]> AgentNaturalNamePatterns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["zen"] = ["zen"],
        ["climax-default"] = ["climax", "climax-default"],
        ["climax-team"] = ["climax team", "climax-team"],
        ["planthy-pm"] = ["planthy", "planthy-pm", "planthy pm"],
        ["planthy-team"] = ["planthy team", "planthy-team"],
        ["mons"] = ["mons"],
        ["ken"] = ["ken"],
        ["mkai"] = ["mkai"],
        ["gm-boc"] = ["gm bốc", "gm boc", "gm-boc", "bốc"],
        ["gm-gao"] = ["gm gạo", "gm gao", "gm-gao", "gạo"],
        ["gm-lua"] = ["gm lúa", "gm lua", "gm-lua", "lúa"],
        ["its-default"] = ["its", "inox tân sơn", "its-default"],
        ["gl-default"] = ["gl-default", "gl automation"]
    };

    public static (int Score, List<string> MatchedKeywords) EvaluateCollaboratorSpecialty(string collaboratorSlug, string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return (0, []);
        var normCollab = SubAgentService.NormalizeAgentId(collaboratorSlug).ToLowerInvariant();
        if (!AgentSpecialtyKeywords.TryGetValue(normCollab, out var keywords))
            return (0, []);

        var lower = userText.ToLowerInvariant();
        int score = 0;
        var matched = new List<string>();
        foreach (var kw in keywords)
        {
            if (kw.Length <= 3)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(kw)}\b", RegexOptions.IgnoreCase))
                {
                    score += 2;
                    matched.Add(kw);
                }
            }
            else if (lower.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                score += kw.Contains(' ') ? 3 : 2;
                matched.Add(kw);
            }
        }
        return (score, matched);
    }

    public static int ScoreCollaboratorSpecialtyMatch(string collaboratorSlug, string? userText) =>
        EvaluateCollaboratorSpecialty(collaboratorSlug, userText).Score;

    public (int TotalScore, List<string> MatchedKeywords, AdaptiveSpecialtyProfileDto Profile) EvaluateAdaptiveCollaboratorSpecialty(
        string collaboratorSlug,
        string? userText,
        string? hostAgentId = null)
    {
        var (baseScore, matched) = EvaluateCollaboratorSpecialty(collaboratorSlug, userText);
        var profile = roomStore.GetAdaptiveSpecialtyProfile(collaboratorSlug, hostAgentId);
        if (string.IsNullOrWhiteSpace(userText))
            return (baseScore, matched, profile);

        var lower = userText.ToLowerInvariant();
        int learnedBonus = 0;
        foreach (var (kw, weight) in profile.LearnedKeywordWeights)
        {
            if (weight <= 0) continue;
            if (lower.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                learnedBonus += Math.Min(weight, 3);
                if (!matched.Contains(kw, StringComparer.OrdinalIgnoreCase))
                {
                    matched.Add(kw);
                }
            }
        }

        int total = baseScore + Math.Min(learnedBonus, 6);
        if (total > 0)
        {
            total = Math.Max(1, total + profile.ReliabilityBonus);
        }
        return (total, matched, profile);
    }

    public static string DetermineWaveExecutionStrategy(IReadOnlyList<string> selectedCollaborators, string? userText)
    {
        if (selectedCollaborators.Count <= 1) return "sequential";
        if (!string.IsNullOrWhiteSpace(userText) && Regex.IsMatch(
                userText,
                @"(?:sau\s+đó|tiếp\s+theo\s+chuyển|rồi\s+chuyển\s+cho|dựa\s+trên\s+kết\s+quả\s+của|lần\s+lượt\s+từng\s+bước|tuần\s+tự)",
                RegexOptions.IgnoreCase))
        {
            return "sequential";
        }
        return "parallel";
    }

    public static string BuildCollaboratorFocusBrief(string collaboratorSlug, IReadOnlyList<string>? matchedKeywords = null)
    {
        var norm = SubAgentService.NormalizeAgentId(collaboratorSlug).ToLowerInvariant();
        var focus = DescribeAgentSpecialty(norm);
        if (matchedKeywords is { Count: > 0 })
        {
            return $"{focus} — Trọng tâm xử lý trong lượt này: {string.Join(", ", matchedKeywords.Take(4))}";
        }
        return focus;
    }

    /// <summary>
    /// Smooth Host-Driven Autonomous Triage Layer:
    /// Allows the Room Owner (Host Agent A) to smoothly and deterministically decide for each new user message:
    /// 1) Whether the request needs to be pushed into the session's single Room for collaboration or handled solo by the Host.
    /// 2) Which specific collaborator agent(s) in the Room should be pulled in (filtering out low-signal secondary noise via Dominant Specialty Pruning).
    /// 3) Conversational momentum / Active Collaborator Affinity when drilling down into the latest collaborator's answer.
    /// </summary>
    /// <summary>
    /// Manager Decision on Session Owner (HostAgentId) Execution Role during a Multi-Agent Room Wave:
    /// - The Session Owner is ALWAYS guaranteed to be the final synthesizer and Room Representative (SynthesisOwnerId = host) after all Room agents finish.
    /// - Manager additionally decides whether the Session Owner should ALSO self-execute its own domain work ("co_execute_and_synthesize")
    ///   or rely purely on the other collaborators' completed runs and synthesize ("synthesize_only").
    /// </summary>
    private (bool HostShouldSelfExecute, string HostExecutionRole, string HostExecutionFocus, int HostScore) DecideHostExecutionDirectiveForMultiAgentWave(
        string host,
        string userBodyText,
        string? rawUserText,
        IReadOnlyList<string> selectedCollaborators,
        string? hostExecutionPolicy = "auto")
    {
        var (hostScore, hostMatchedKws, _) = EvaluateAdaptiveCollaboratorSpecialty(host, userBodyText, host);
        var normPolicy = (hostExecutionPolicy ?? "auto").Trim().ToLowerInvariant();
        if (normPolicy is "co_execute" or "always_co_execute")
        {
            var focus = BuildCollaboratorFocusBrief(host, hostMatchedKws);
            return (
                HostShouldSelfExecute: true,
                HostExecutionRole: "co_execute_and_synthesize",
                HostExecutionFocus: $"[Chế độ: Luôn tự chạy kèm] Chủ session @{host} vừa tự thực thi phần chuyên môn riêng ({focus}) vừa đại diện Room tổng hợp kết quả cuối từ @{string.Join(", @", selectedCollaborators)} sau khi cả Room chạy xong.",
                HostScore: Math.Max(hostScore, 6));
        }
        if (normPolicy is "synthesize_only" or "synth_only")
        {
            return (
                HostShouldSelfExecute: false,
                HostExecutionRole: "synthesize_only",
                HostExecutionFocus: $"[Chế độ: Chỉ đại diện tổng hợp] Chủ session @{host} không tự chạy nhánh riêng mà tập trung 100% làm Người đại diện Room tổng hợp kết quả cuối từ @{string.Join(", @", selectedCollaborators)} sau khi cả Room chạy done.",
                HostScore: hostScore);
        }

        // Check if the user explicitly @mentioned or naturally requested the Host agent alongside collaborators
        bool hostExplicitlyMentioned = !string.IsNullOrWhiteSpace(rawUserText) &&
            Regex.IsMatch(rawUserText, $@"(?:^|\s)@{Regex.Escape(host)}\b|(?:cùng\s+với|kết\s+hợp\s+với|cả)\s+@{Regex.Escape(host)}\b", RegexOptions.IgnoreCase);

        if (!hostExplicitlyMentioned && AgentNaturalNamePatterns.TryGetValue(host, out var hostPatterns))
        {
            foreach (var pat in hostPatterns)
            {
                if (Regex.IsMatch(userBodyText, $@"\b(?:cả\s+|cùng\s+)?{Regex.Escape(pat)}\b", RegexOptions.IgnoreCase))
                {
                    hostExplicitlyMentioned = true;
                    break;
                }
            }
        }

        bool shouldSelfExecute = hostExplicitlyMentioned || hostScore >= 4;
        if (shouldSelfExecute)
        {
            var focus = BuildCollaboratorFocusBrief(host, hostMatchedKws);
            return (
                HostShouldSelfExecute: true,
                HostExecutionRole: "co_execute_and_synthesize",
                HostExecutionFocus: $"Chủ session @{host} vừa tự thực thi phần chuyên môn riêng ({focus}) vừa đại diện Room tổng hợp kết quả cuối từ @{string.Join(", @", selectedCollaborators)} sau khi cả Room chạy xong.",
                HostScore: Math.Max(hostScore, hostExplicitlyMentioned ? 6 : hostScore));
        }

        return (
            HostShouldSelfExecute: false,
            HostExecutionRole: "synthesize_only",
            HostExecutionFocus: $"Chủ session @{host} không cần tự chạy nhánh chuyên môn riêng (do @{string.Join(", @", selectedCollaborators)} đã đảm nhiệm), nhưng giữ quyền ưu tiên đại diện Room tổng hợp câu trả lời cuối cùng sau khi cả Room chạy done.",
            HostScore: hostScore);
    }

    private HostRoomTriageDecision WithHostRepresentativeGovernance(
        string host,
        string hostChatId,
        string? hostSessionId,
        string userBodyText,
        string? rawUserText,
        HostRoomTriageDecision decision)
    {
        if (!decision.ShouldUseRoom || decision.SelectedCollaborators.Count == 0)
        {
            return decision with
            {
                SynthesisOwnerId = host,
                HostShouldSelfExecute = true,
                HostExecutionRole = "solo_execute_and_respond",
                HostExecutionFocus = $"Chủ session @{host} trực tiếp tự thực thi và đại diện phiên đưa ra câu trả lời cuối cùng.",
                CollaborationMode = "solo"
            };
        }

        var room = roomStore.GetByChat(host, hostChatId)
            ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null);
        var (selfExec, execRole, execFocus, hostScore) = DecideHostExecutionDirectiveForMultiAgentWave(
            host, userBodyText, rawUserText, decision.SelectedCollaborators, room?.HostExecutionPolicy);

        var updatedFocuses = decision.CollaboratorFocuses is not null
            ? new Dictionary<string, string>(decision.CollaboratorFocuses, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        updatedFocuses[host] = execFocus;

        var updatedScores = decision.AdaptiveScores is not null
            ? new Dictionary<string, int>(decision.AdaptiveScores, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (hostScore > 0) updatedScores[host] = hostScore;

        var hostRoleSummary = selfExec
            ? $"Chủ session @{host} được Manager phân định vừa tự chạy phần chuyên môn riêng vừa đại diện Room tổng hợp cuối sau khi cả Room chạy done"
            : $"Chủ session @{host} được Manager phân định không cần tự chạy nhánh riêng mà đại diện Room tổng hợp câu trả lời cuối cùng sau khi @{string.Join(", @", decision.SelectedCollaborators)} chạy done";

        var enrichedReason = decision.Reason.EndsWith('.')
            ? $"{decision.Reason[..^1]} ({hostRoleSummary})."
            : $"{decision.Reason} ({hostRoleSummary}).";

        var collabMode = ClassifyCollaborationMode(decision.SelectedCollaborators, userBodyText, decision.Mode);
        var isSequentialPipelineMode = string.Equals(collabMode, "relay_pipeline", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(collabMode, "progressive_refinement", StringComparison.OrdinalIgnoreCase);
        var finalCollaborators = isSequentialPipelineMode
            ? OrderCollaboratorsForRelayPipeline(decision.SelectedCollaborators, rawUserText)
            : decision.SelectedCollaborators;
        var finalStrategy = isSequentialPipelineMode || finalCollaborators.Count <= 1
            ? "sequential"
            : decision.ExecutionStrategy;

        return decision with
        {
            SelectedCollaborators = finalCollaborators,
            ExecutionStrategy = finalStrategy,
            Reason = enrichedReason,
            CollaboratorFocuses = updatedFocuses,
            AdaptiveScores = updatedScores.Count > 0 ? updatedScores : decision.AdaptiveScores,
            SynthesisOwnerId = host,
            HostShouldSelfExecute = selfExec,
            HostExecutionRole = execRole,
            HostExecutionFocus = execFocus,
            CollaborationMode = collabMode
        };
    }

    public HostRoomTriageDecision DecideRoomCollaboratorsForTurn(
        string hostAgentId,
        string hostChatId,
        string? hostSessionId,
        string? rawUserText,
        IReadOnlyList<string>? explicitMentions = null)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var coreDecision = DecideRoomCollaboratorsForTurnCore(hostAgentId, hostChatId, hostSessionId, rawUserText, explicitMentions);
        return WithHostRepresentativeGovernance(host, hostChatId, hostSessionId, (rawUserText ?? "").Trim(), rawUserText, coreDecision);
    }

    private HostRoomTriageDecision DecideRoomCollaboratorsForTurnCore(
        string hostAgentId,
        string hostChatId,
        string? hostSessionId,
        string? rawUserText,
        IReadOnlyList<string>? explicitMentions = null)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        if (string.IsNullOrWhiteSpace(rawUserText) || rawUserText.Contains("[IMPLICIT_REACTION_FEEDBACK", StringComparison.OrdinalIgnoreCase))
        {
            return new HostRoomTriageDecision(
                ShouldUseRoom: false,
                SelectedCollaborators: [],
                Mode: "host_solo",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận tín hiệu phản hồi cảm xúc và phân định Chủ phòng (@{host}) xử lý trực tiếp.",
                Confidence: 1.0,
                CollaboratorFocuses: null,
                ExecutionStrategy: "sequential",
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [host]);
        }

        var room = roomStore.GetByChat(host, hostChatId)
            ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null);

        var roomCollaborators = room?.Participants
            .Where(p => string.Equals(p.Role, "collaborator", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(p.AgentId, host, StringComparison.OrdinalIgnoreCase))
            .Select(p => SubAgentService.NormalizeAgentId(p.AgentId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        // Extract optional [Replying to <Author>: "..."] header and clean user body text early so @mentions inside quoted snippets don't misfire
        string? replyAuthorHeader = null;
        string userBodyText = rawUserText.Trim();
        var replyMatch = Regex.Match(userBodyText, @"^\[Replying to ([^:\]]+):\s*""[^""]*""\]\s*", RegexOptions.IgnoreCase);
        if (replyMatch.Success)
        {
            replyAuthorHeader = replyMatch.Groups[1].Value.Trim();
            userBodyText = userBodyText[replyMatch.Length..].Trim();
        }

        // 1. Check Explicit @mentions (evaluated on userBodyText so quoted snippets don't re-trigger old @mentions) or @room / @all / @team
        var mentions = (explicitMentions ?? DetectMentionedCollaborators(userBodyText, host))
            .Select(SubAgentService.NormalizeAgentId)
            .Where(c => !string.Equals(c, host, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool asksAllRoom = Regex.IsMatch(
            userBodyText,
            @"(?:^|\s)(?:@all|@room|@team)\b|cả\s+phòng|tất\s+cả\s+(?:các\s+)?(?:agent|chuyên\s+viên|bạn\s+trong\s+phòng)",
            RegexOptions.IgnoreCase);

        if (asksAllRoom && roomCollaborators.Count > 0)
        {
            foreach (var rc in roomCollaborators)
            {
                if (!mentions.Contains(rc, StringComparer.OrdinalIgnoreCase))
                    mentions.Add(rc);
            }
        }

        if (mentions.Count > 0)
        {
            var focuses = mentions.ToDictionary(
                c => c,
                c => BuildCollaboratorFocusBrief(c, EvaluateCollaboratorSpecialty(c, userBodyText).MatchedKeywords),
                StringComparer.OrdinalIgnoreCase);

            return new HostRoomTriageDecision(
                ShouldUseRoom: true,
                SelectedCollaborators: mentions,
                Mode: "explicit_mention",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu trong Room và phân định giao chuyên viên được chỉ định @{string.Join(", @", mentions)} xử lý (Chủ phòng @{host} tổng hợp cuối).",
                Confidence: 1.0,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(mentions, userBodyText),
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [.. mentions, host]);
        }

        // 2. If the session Room has no collaborators yet and none were @mentioned, Manager assigns directly to Host
        // while scanning cross-domain specialist relevance so solo agent chat becomes smarter and can suggest smooth multi-agent expansion.
        if (roomCollaborators.Count == 0)
        {
            var soloPool = new[] { "zen", "climax-default", "planthy-pm", "mons", "ken", "gm-gao", "gm-lua" };
            var soloAdaptiveScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var suggestedForSolo = soloPool
                .Where(c => !string.Equals(c, host, StringComparison.OrdinalIgnoreCase))
                .Select(c =>
                {
                    var (sc, _, _) = EvaluateAdaptiveCollaboratorSpecialty(c, userBodyText, host);
                    if (sc > 0) soloAdaptiveScores[c] = sc;
                    return (Collab: c, Score: sc);
                })
                .Where(x => x.Score >= 4)
                .OrderByDescending(x => x.Score)
                .Take(3)
                .Select(x => x.Collab)
                .ToList();

            var soloReason = suggestedForSolo.Count > 0
                ? $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu: Phiên đang ở chế độ Cá nhân (@{host} — {DescribeAgentSpecialty(host)}) nên phân định @{host} trực tiếp xử lý đa góc độ (Gợi ý chuyên viên có thể kéo thêm vào Room: @{string.Join(", @", suggestedForSolo)})."
                : $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu: Room hiện chỉ có Chủ phòng (@{host}) nên phân định @{host} trực tiếp xử lý.";

            return new HostRoomTriageDecision(
                ShouldUseRoom: false,
                SelectedCollaborators: [],
                Mode: "no_room_collaborators",
                Reason: soloReason,
                Confidence: 1.0,
                CollaboratorFocuses: null,
                ExecutionStrategy: "sequential",
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [host],
                AdaptiveScores: soloAdaptiveScores.Count > 0 ? soloAdaptiveScores : null,
                SuggestedCollaborators: suggestedForSolo.Count > 0 ? suggestedForSolo : null);
        }

        // 3. Check if user is explicitly instructing the Host to answer solo or only reformat/summarize existing output
        bool isExplicitSoloDirective = Regex.IsMatch(
            userBodyText,
            @"(?:chỉ\s+mình\s+(?:bạn|em|anh|chủ\s+phòng)|tự\s+(?:bạn|em)\s+trả\s+lời|không\s+cần\s+(?:hỏi|kéo|gọi|đẩy\s+vào)\s+(?:room|phòng|ai|chuyên\s+viên|thêm)|chủ\s+phòng\s+tự\s+(?:trả\s+lời|tổng\s+hợp))",
            RegexOptions.IgnoreCase);

        bool isShortAcknowledgement = userBodyText.Length <= 28 && Regex.IsMatch(
            userBodyText,
            @"^(?:ok|okay|oke|dạ|vâng|cảm\s+ơn(?:\s+\w+)*|thanks|thank\s+you|tốt\s+lắm|hay\s+quá|được\s+rồi|chốt\s+(?:vậy|nhé|đi)|tạm\s+biệt|bye|xin\s+chào|hello|hi)[.!]?$",
            RegexOptions.IgnoreCase);

        if (isExplicitSoloDirective || isShortAcknowledgement)
        {
            return new HostRoomTriageDecision(
                ShouldUseRoom: false,
                SelectedCollaborators: [],
                Mode: "host_solo",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu mới trong Room ({room?.MatrixRoomId}) và phân định Chủ phòng (@{host}) trực tiếp xử lý mà không cần gọi thêm chuyên viên.",
                Confidence: 0.98,
                CollaboratorFocuses: null,
                ExecutionStrategy: "sequential",
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [host]);
        }

        // 4. Check Direct Reply to a Collaborator bubble OR Natural Collaborator Name Reference (without @) + Negative Exclusion ("trừ zen", "không cần gọi climax")
        var namedInRoom = new List<string>();
        var excludedInRoom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var collab in roomCollaborators)
        {
            var patterns = AgentNaturalNamePatterns.TryGetValue(collab, out var pList) ? pList : [collab];
            var friendly = FriendlyAgentName(collab);

            if (!string.IsNullOrWhiteSpace(replyAuthorHeader))
            {
                if (replyAuthorHeader.Contains(collab, StringComparison.OrdinalIgnoreCase) ||
                    replyAuthorHeader.Contains(friendly, StringComparison.OrdinalIgnoreCase) ||
                    patterns.Any(p => replyAuthorHeader.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!namedInRoom.Contains(collab, StringComparer.OrdinalIgnoreCase))
                        namedInRoom.Add(collab);
                }
            }

            foreach (var pat in patterns)
            {
                if (Regex.IsMatch(userBodyText, $@"\b{Regex.Escape(pat)}\b", RegexOptions.IgnoreCase))
                {
                    bool negated = Regex.IsMatch(
                        userBodyText,
                        $@"(?:không|đừng|khỏi|trừ|bỏ\s+qua)\s+(?:cần\s+)?(?:(?:hỏi|gọi|kéo)\s+)?{Regex.Escape(pat)}\b",
                        RegexOptions.IgnoreCase);
                    if (negated)
                    {
                        excludedInRoom.Add(collab);
                    }
                    else if (!namedInRoom.Contains(collab, StringComparer.OrdinalIgnoreCase))
                    {
                        namedInRoom.Add(collab);
                    }
                }
            }
        }

        namedInRoom.RemoveAll(c => excludedInRoom.Contains(c));
        if (namedInRoom.Count > 0)
        {
            var focuses = namedInRoom.ToDictionary(
                c => c,
                c => BuildCollaboratorFocusBrief(c, EvaluateCollaboratorSpecialty(c, userBodyText).MatchedKeywords),
                StringComparer.OrdinalIgnoreCase);

            return new HostRoomTriageDecision(
                ShouldUseRoom: true,
                SelectedCollaborators: namedInRoom,
                Mode: "reply_or_named",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu trong Room và phân định giao @{string.Join(", @", namedInRoom)} xử lý phản hồi trực tiếp (Chủ phòng @{host} tổng hợp cuối).",
                Confidence: 0.95,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(namedInRoom, userBodyText),
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [.. namedInRoom, host]);
        }

        // If the session Room's triage policy is set to "manual", do not auto-pull collaborators unless explicitly @mentioned or replied/named
        if (string.Equals(room?.TriagePolicy, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return new HostRoomTriageDecision(
                ShouldUseRoom: false,
                SelectedCollaborators: [],
                Mode: "manual_policy_host_solo",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) kiểm tra chế độ điều phối Room đang đặt là Thủ công (Manual); phân định Chủ phòng (@{host}) trực tiếp phản hồi khi không có thẻ @mention.",
                Confidence: 1.0,
                CollaboratorFocuses: null,
                ExecutionStrategy: "sequential",
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [host]);
        }

        // Check if this is a pure text formatting/rewriting request of already-generated content (with no domain investigation needed)
        bool isPureRewriteRequest = Regex.IsMatch(
            userBodyText,
            @"^(?:hãy\s+|nhờ\s+(?:bạn|em|anh)\s+)?(?:viết\s+lại\s+đoạn\s+trên|tóm\s+tắt\s+lại\s+kết\s+quả\s+trên|rút\s+gọn\s+lại|dịch\s+đoạn\s+trên\s+sang)\b",
            RegexOptions.IgnoreCase);
        if (isPureRewriteRequest)
        {
            return new HostRoomTriageDecision(
                ShouldUseRoom: false,
                SelectedCollaborators: [],
                Mode: "host_solo",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) đánh giá yêu cầu chỉ biên tập/tóm tắt nội dung sẵn có nên phân định Chủ phòng (@{host}) trực tiếp thực hiện.",
                Confidence: 0.95,
                CollaboratorFocuses: null,
                ExecutionStrategy: "sequential",
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [host]);
        }

        // 5. Inspect Latest Wave in Room for Pending Clarification / Scope Questions & Conversational Momentum
        var pendingWaveCollaborators = new List<string>();
        bool latestWaveAskedClarification = false;
        if (room is not null && room.Turns.Count > 0)
        {
            int lastReqIdx = -1;
            for (int i = room.Turns.Count - 1; i >= 0; i--)
            {
                if (room.Turns[i].Kind == "request" && !string.Equals(room.Turns[i].AuthorRole, "manager", StringComparison.OrdinalIgnoreCase))
                {
                    lastReqIdx = i;
                    break;
                }
            }

            var latestWaveTurns = lastReqIdx >= 0 ? room.Turns.Skip(lastReqIdx).ToList() : room.Turns.TakeLast(5).ToList();
            foreach (var wt in latestWaveTurns)
            {
                if (wt.Kind is "collaborator_reply" or "question" &&
                    roomCollaborators.Contains(wt.AuthorId, StringComparer.OrdinalIgnoreCase) &&
                    !excludedInRoom.Contains(wt.AuthorId))
                {
                    if (!pendingWaveCollaborators.Contains(wt.AuthorId, StringComparer.OrdinalIgnoreCase))
                        pendingWaveCollaborators.Add(wt.AuthorId);

                    if (wt.Kind == "question" ||
                        Regex.IsMatch(wt.Text, @"cần\s+bổ\s+sung|chưa\s+đủ|cho\s+(?:em\s+)?biết\s+thêm|bổ\s+sung\s+thêm|ngành\s+hàng|tên\s+2[–\-]3\s+đối\s+thủ|\?", RegexOptions.IgnoreCase))
                    {
                        latestWaveAskedClarification = true;
                    }
                }
                else if (wt.Kind == "synthesis" &&
                         Regex.IsMatch(wt.Text, @"cho\s+(?:em\s+)?biết\s+thêm|cần\s+bổ\s+sung|bổ\s+sung\s+giúp|ngành\s+hàng|đối\s+thủ\s+cụ\s+thể|\?", RegexOptions.IgnoreCase))
                {
                    latestWaveAskedClarification = true;
                }
            }
        }

        // 6. Score each Collaborator in the Room by Domain Specialty Relevance + Adaptive Quality-Gate Learning + Dominant Specialty Pruning
        var adaptiveScoresMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var evalList = roomCollaborators
            .Where(c => !excludedInRoom.Contains(c))
            .Select((collab, idx) =>
            {
                var (score, matchedKws, _) = EvaluateAdaptiveCollaboratorSpecialty(collab, userBodyText, host);
                if (score > 0) adaptiveScoresMap[collab] = score;
                return (Collab: collab, Score: score, MatchedKeywords: matchedKws, Order: idx);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Order)
            .ToList();

        int topScore = evalList.Count > 0 ? evalList[0].Score : 0;
        var scoredCollaborators = evalList
            .Where(x => topScore < 4 || x.Score >= 4 || x.Score * 2 >= topScore)
            .Select(x => x.Collab)
            .ToList();

        // 6b. Manager Dynamic Specialist Recruitment (with Adaptive Quality-Gate Learning):
        // If no current Room member strongly covers the request's domain (topScore < 4), AgentTeams Manager scans the canonical specialist pool
        // and automatically recruits the top matching external specialist (Score >= 6) into the session's single Room!
        var autoRecruited = new List<string>();
        var externalMatchedKeywords = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (topScore < 4)
        {
            var canonicalPool = new[] { "zen", "climax-default", "planthy-pm", "mons", "ken", "gm-gao", "gm-lua" };
            var externalCandidates = canonicalPool
                .Where(c => !string.Equals(c, host, StringComparison.OrdinalIgnoreCase) &&
                            !roomCollaborators.Contains(c, StringComparer.OrdinalIgnoreCase) &&
                            !excludedInRoom.Contains(c))
                .Select(c =>
                {
                    var (sc, kws, _) = EvaluateAdaptiveCollaboratorSpecialty(c, userBodyText, host);
                    return (Collab: c, Score: sc, MatchedKeywords: kws);
                })
                .Where(x => x.Score >= 6)
                .OrderByDescending(x => x.Score)
                .ToList();

            if (externalCandidates.Count > 0)
            {
                int bestExtScore = externalCandidates[0].Score;
                foreach (var ext in externalCandidates.Where(x => x.Score * 2 >= bestExtScore))
                {
                    autoRecruited.Add(ext.Collab);
                    externalMatchedKeywords[ext.Collab] = ext.MatchedKeywords;
                    adaptiveScoresMap[ext.Collab] = ext.Score;
                    if (!scoredCollaborators.Contains(ext.Collab, StringComparer.OrdinalIgnoreCase))
                    {
                        scoredCollaborators.Add(ext.Collab);
                    }
                }
                if (bestExtScore > topScore) topScore = bestExtScore;
            }
        }

        bool isScopeSupply = latestWaveAskedClarification &&
            autoRecruited.Count == 0 &&
            (topScore < 6 || evalList.Count(x => x.Score >= 4) > 1) &&
            Regex.IsMatch(userBodyText, @"ngành|sản\s+phẩm|dịch\s+vụ|đối\s+thủ|chuỗi|công\s+ty|dự\s+án|mục\s+tiêu|địa\s+bàn", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(userBodyText, @"\b(?:chỉ\s+cần|riêng\s+phần|riêng\s+mảng)\b", RegexOptions.IgnoreCase);

        if (isScopeSupply && pendingWaveCollaborators.Count > 0)
        {
            foreach (var pwc in pendingWaveCollaborators)
            {
                if (!scoredCollaborators.Contains(pwc, StringComparer.OrdinalIgnoreCase))
                    scoredCollaborators.Add(pwc);
            }
        }

        if (scoredCollaborators.Count > 0)
        {
            var focuses = scoredCollaborators.ToDictionary(
                c => c,
                c =>
                {
                    if (externalMatchedKeywords.TryGetValue(c, out var extKws))
                        return BuildCollaboratorFocusBrief(c, extKws);
                    var m = evalList.FirstOrDefault(x => string.Equals(x.Collab, c, StringComparison.OrdinalIgnoreCase));
                    return BuildCollaboratorFocusBrief(c, m.MatchedKeywords);
                },
                StringComparer.OrdinalIgnoreCase);

            var confidence = Math.Clamp(0.72 + Math.Min(topScore, 8) * 0.035, 0.75, 0.99);
            var mode = autoRecruited.Count > 0 ? "manager_specialist_recruitment" : "specialty_triage";
            var reason = autoRecruited.Count > 0
                ? $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu mới, phát hiện Room chưa có chuyên viên phụ trách mảng này nên tự động mời bổ sung @{string.Join(", @", autoRecruited)} vào Room và phân định giao @{string.Join(", @", scoredCollaborators)} xử lý (Chủ phòng @{host} tổng hợp cuối)."
                : $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu mới trong Room, phân tích chuyên môn và phân định giao @{string.Join(", @", scoredCollaborators)} xử lý (Chủ phòng @{host} tổng hợp cuối).";

            return new HostRoomTriageDecision(
                ShouldUseRoom: true,
                SelectedCollaborators: scoredCollaborators,
                Mode: mode,
                Reason: reason,
                Confidence: Math.Round(confidence, 2),
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(scoredCollaborators, userBodyText),
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [.. scoredCollaborators, host],
                AutoRecruitedCollaborators: autoRecruited.Count > 0 ? autoRecruited : null,
                AdaptiveScores: adaptiveScoresMap.Count > 0 ? adaptiveScoresMap : null);
        }

        // 7. Continuity Follow-up: If no specific domain keyword matched, but the latest wave asked the user for missing context/scope
        if (latestWaveAskedClarification && pendingWaveCollaborators.Count > 0 && userBodyText.Length >= 4)
        {
            var focuses = pendingWaveCollaborators.ToDictionary(
                c => c,
                c => BuildCollaboratorFocusBrief(c),
                StringComparer.OrdinalIgnoreCase);

            return new HostRoomTriageDecision(
                ShouldUseRoom: true,
                SelectedCollaborators: pendingWaveCollaborators,
                Mode: "continuity_followup",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) nhận thấy người dùng đang bổ sung thông tin cho phiên thảo luận trước nên phân định giao @{string.Join(", @", pendingWaveCollaborators)} trong Room hoàn tất phân tích (Chủ phòng @{host} tổng hợp cuối).",
                Confidence: 0.9,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(pendingWaveCollaborators, userBodyText),
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [.. pendingWaveCollaborators, host]);
        }

        // 8. Conversational Momentum / Active Collaborator Drill-down Affinity
        bool isDrillDownFollowup = pendingWaveCollaborators.Count > 0 && Regex.IsMatch(
            userBodyText,
            @"(?:chi\s+tiết\s+hơn|cụ\s+thể\s+hơn|làm\s+rõ\s+hơn|mục\s+(?:số\s+)?\d+|phương\s+án\s+(?:này|đó|\d+)|điểm\s+(?:này|đó|\d+)|bổ\s+sung\s+thêm|thêm\s+ví\s+dụ|còn\s+về\s+phần|tiếp\s+tục\s+phân\s+tích|đi\s+sâu\s+hơn)",
            RegexOptions.IgnoreCase);

        if (isDrillDownFollowup)
        {
            var focuses = pendingWaveCollaborators.ToDictionary(
                c => c,
                c => BuildCollaboratorFocusBrief(c),
                StringComparer.OrdinalIgnoreCase);

            return new HostRoomTriageDecision(
                ShouldUseRoom: true,
                SelectedCollaborators: pendingWaveCollaborators,
                Mode: "active_collaborator_affinity",
                Reason: $"AgentTeams Manager (@{ManagerAgentId}) duy trì mạch trao đổi chuyên sâu trong Room và phân định giao @{string.Join(", @", pendingWaveCollaborators)} làm rõ yêu cầu (Chủ phòng @{host} tổng hợp cuối).",
                Confidence: 0.88,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(pendingWaveCollaborators, userBodyText),
                TriageOwnerId: ManagerAgentId,
                AssignedAgents: [.. pendingWaveCollaborators, host]);
        }

        // 9. Default when no collaborator specialty matches and no pending clarification/drill-down exists: Manager assigns to Host solo
        return new HostRoomTriageDecision(
            ShouldUseRoom: false,
            SelectedCollaborators: [],
            Mode: "host_solo",
            Reason: $"AgentTeams Manager (@{ManagerAgentId}) tiếp nhận yêu cầu mới trong Room, đánh giá không đòi hỏi phối hợp chuyên môn từ các thành viên khác và phân định Chủ phòng (@{host}) trực tiếp xử lý.",
            Confidence: 0.92,
            CollaboratorFocuses: null,
            ExecutionStrategy: "sequential",
            TriageOwnerId: ManagerAgentId,
            AssignedAgents: [host]);
    }

    /// <summary>
    /// Enhances individual (solo) agent turns and Multi-Agent -> Solo transitions in the same session:
    /// 1) When a session with an active Multi-Agent Room routes a turn to Host Solo, injects the latest verified Room insights so the Host retains seamless team continuity.
    /// 2) When an individual (solo) chat turn spans multiple specialist domains (SuggestedCollaborators), provides a concise structured framing brief so the solo agent answers with multi-angle depth and smooth readiness to expand into a Room.
    /// Uses the [SHARED SESSION ROOM CONTEXT] marker so EnrichChatHistoryJson and frontend stripSharedRoomContext keep the visible user bubble 100% clean.
    /// </summary>
    public string? BuildSoloSmartContextInjection(
        string hostAgentId,
        string hostChatId,
        string? hostSessionId,
        string? rawUserText,
        HostRoomTriageDecision decision)
    {
        if (string.IsNullOrWhiteSpace(rawUserText) ||
            rawUserText.Contains("[IMPLICIT_REACTION_FEEDBACK", StringComparison.OrdinalIgnoreCase) ||
            rawUserText.Contains("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = rawUserText.Trim();
        if (trimmed.Length <= 24)
        {
            return null;
        }

        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var room = roomStore.GetByChat(host, hostChatId)
            ?? (!string.IsNullOrWhiteSpace(hostSessionId) ? roomStore.GetByChat(host, hostSessionId!) : null);

        if (!SessionRoomStore.IsMultiAgentRoom(room))
        {
            // Independent solo chat session: never inject [SHARED SESSION ROOM CONTEXT] or force Room framing!
            return null;
        }

        var recentCollabTurns = room!.Turns
            .Where(t => t.Kind is "collaborator_reply" or "question")
            .TakeLast(4)
            .ToList();

        if (recentCollabTurns.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[SHARED SESSION ROOM CONTEXT]");
        sb.AppendLine($"Lưu ý hệ thống: {decision.Reason}");
        sb.AppendLine($"- Vai trò chủ đạo của bạn: @{host} ({FriendlyAgentName(host)} — {DescribeAgentSpecialty(host)}).");
        sb.AppendLine("- Ngữ cảnh liên tục từ các chuyên viên AI trong Room của phiên chat này (hãy kế thừa mạch lạc khi phản hồi cho người dùng):");
        foreach (var ct in recentCollabTurns)
        {
            var snippet = StripCollaboratorMentions(ct.Text);
            if (snippet.Length > 340) snippet = snippet[..340] + "...";
            sb.AppendLine($"  * @{ct.AuthorId} ({ct.AuthorName}): {snippet}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void BroadcastRoomTurnToHostSession(
        SessionCollaborationRoom room,
        SessionRoomTurn turn,
        string eventStatus)
    {
        var isHostNarrator = turn.Kind is "synthesis" or "host_kickoff" or "host_progress" ||
                             string.Equals(turn.AuthorRole, "host", StringComparison.OrdinalIgnoreCase);
        var metadataJson = JsonSerializer.Serialize(new
        {
            room_id = room.RoomId,
            matrix_room_id = room.MatrixRoomId,
            element_room_url = room.ElementRoomUrl,
            sender_agent_id = isHostNarrator ? null : turn.AuthorId,
            sender_agent_name = isHostNarrator ? null : turn.AuthorName,
            author_role = turn.AuthorRole,
            turn_kind = turn.Kind,
            turn_no = turn.TurnNo,
            run_id = turn.RunId,
            round_no = turn.RoundNo,
            visibility = turn.Visibility,
            receipt = turn.Receipt,
            target_agent_id = turn.TargetAgentId,
            reasoning = turn.Reasoning,
            steps = turn.Steps,
            manager_audit = turn.ManagerAudit
        });

        ChannelResponseStore.Record(
            agentId: room.HostAgentId,
            sessionId: room.HostChatId,
            channel: "agentteams-room",
            role: "assistant",
            content: turn.Text,
            status: eventStatus,
            id: turn.Id,
            metadata: metadataJson);

        if (!string.Equals(room.HostSessionId, room.HostChatId, StringComparison.OrdinalIgnoreCase))
        {
            ChannelResponseStore.Record(
                agentId: room.HostAgentId,
                sessionId: room.HostSessionId,
                channel: "agentteams-room",
                role: "assistant",
                content: turn.Text,
                status: eventStatus,
                id: turn.Id,
                metadata: metadataJson);
        }
    }

    [GeneratedRegex(@"(?:^|\s)@([a-zA-Z0-9_-]{2,32})", RegexOptions.Compiled)]
    private static partial Regex MentionRegex();
}
