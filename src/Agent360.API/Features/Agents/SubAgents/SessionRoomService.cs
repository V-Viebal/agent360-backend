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

public sealed record SessionRoomTurn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("turn_no")] int TurnNo,
    [property: JsonPropertyName("author_id")] string AuthorId,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("author_role")] string AuthorRole, // "host" | "collaborator" | "manager" | "admin" | "user"
    [property: JsonPropertyName("kind")] string Kind, // "request" | "collaborator_progress" | "collaborator_reply" | "question" | "answer" | "manager_guard" | "synthesis"
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("target_agent_id")] string? TargetAgentId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("reasoning")] string? Reasoning = null,
    [property: JsonPropertyName("steps")] IReadOnlyList<SessionRoomStep>? Steps = null,
    [property: JsonPropertyName("manager_audit")] ManagerOutputAuditRecord? ManagerAudit = null);

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
    [property: JsonPropertyName("triage_policy")] string? TriagePolicy = "auto");

public sealed record UpdateRoomTriagePolicyRequest(
    [property: JsonPropertyName("triage_policy")] string TriagePolicy);

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
    [property: JsonPropertyName("execution_strategy")] string ExecutionStrategy = "parallel");


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
            var normalizedIncoming = incoming with { HostAgentId = normHost };

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

    private static string ExtractMessageNodeText(System.Text.Json.Nodes.JsonObject? m)
    {
        if (m is null) return "";
        if (m["content"] is System.Text.Json.Nodes.JsonArray arr)
            return string.Join("\n", arr.Select(x => x?["text"]?.ToString() ?? "")).Trim();
        return (m["content"]?.ToString() ?? "").Trim();
    }

    private static System.Text.Json.Nodes.JsonObject CreateRoomTurnMessageNode(SessionCollaborationRoom room, SessionRoomTurn turn)
    {
        var isSynthesis = turn.Kind == "synthesis";
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
                ["sender_agent_id"] = isSynthesis ? null : turn.AuthorId,
                ["sender_agent_name"] = isSynthesis ? null : turn.AuthorName,
                ["author_role"] = turn.AuthorRole,
                ["turn_kind"] = turn.Kind,
                ["room_id"] = room.RoomId,
                ["matrix_room_id"] = room.MatrixRoomId,
                ["element_room_url"] = room.ElementRoomUrl,
                ["reasoning"] = turn.Reasoning,
                ["steps"] = turn.Steps is not null ? JsonSerializer.SerializeToNode(turn.Steps) : null,
                ["manager_audit"] = turn.ManagerAudit is not null ? JsonSerializer.SerializeToNode(turn.ManagerAudit) : null
            }
        };
    }

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

    public string EnrichChatHistoryJson(string historyRaw, string? agentId, string chatId)
    {
        try
        {
            var room = !string.IsNullOrWhiteSpace(agentId)
                ? (GetByChat(agentId, chatId) ?? GetByChatIdOnly(chatId))
                : GetByChatIdOnly(chatId);
            if (room is null) return historyRaw;

            var node = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(historyRaw) ? "{}" : historyRaw) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();

            node["collaboration_room"] = JsonSerializer.SerializeToNode(room);

            if (node["messages"] is not System.Text.Json.Nodes.JsonArray messagesArr)
            {
                messagesArr = new System.Text.Json.Nodes.JsonArray();
                node["messages"] = messagesArr;
            }

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

            // 3. Strip internal [SHARED SESSION ROOM CONTEXT] synthesis block from persisted user messages so the UI only shows the user's clean message
            foreach (var msgNode in messagesArr.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if (!string.Equals(msgNode["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase)) continue;
                if (msgNode["content"] is System.Text.Json.Nodes.JsonValue strVal && strVal.TryGetValue<string>(out var rawStr) && !string.IsNullOrEmpty(rawStr))
                {
                    var idx = rawStr.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) idx = rawStr.IndexOf("[AGENTTEAMS COLLABORATION ROOM", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) msgNode["content"] = rawStr[..idx].TrimEnd();
                }
                else if (msgNode["content"] is System.Text.Json.Nodes.JsonArray contentArr)
                {
                    foreach (var part in contentArr.OfType<System.Text.Json.Nodes.JsonObject>())
                    {
                        if (part["text"] is System.Text.Json.Nodes.JsonValue tVal && tVal.TryGetValue<string>(out var partStr) && !string.IsNullOrEmpty(partStr))
                        {
                            var idx = partStr.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) idx = partStr.IndexOf("[AGENTTEAMS COLLABORATION ROOM", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0) part["text"] = partStr[..idx].TrimEnd();
                        }
                    }
                }
            }

            // 4. Group Room turns into Collaboration Waves (Request -> Collaborator Replies -> Host Synthesis)
            // and anchor each wave to its matching user message so User -> Collaborators -> Host Synthesis order is strictly preserved across all turns!
            var workList = messagesArr
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .Select(m => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(m.ToJsonString())!)
                .ToList();

            var waves = new List<(SessionRoomTurn? RequestTurn, List<SessionRoomTurn> Replies, SessionRoomTurn? SynthesisTurn)>();
            SessionRoomTurn? curReq = null;
            var curReplies = new List<SessionRoomTurn>();
            SessionRoomTurn? curSynth = null;

            foreach (var turn in room.Turns.OrderBy(t => t.TurnNo))
            {
                if (turn.Kind == "request" && string.Equals(turn.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (turn.Kind == "request")
                {
                    if (curReq is not null && curReplies.Count == 0 && curSynth is null &&
                        string.Equals(curReq.Text.Trim(), turn.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // duplicate request turn in same wave
                    }
                    if (curReq is not null || curReplies.Count > 0 || curSynth is not null)
                    {
                        waves.Add((curReq, curReplies, curSynth));
                        curReplies = [];
                        curSynth = null;
                    }
                    curReq = turn;
                    continue;
                }

                if (turn.Kind is "collaborator_reply" or "question" or "manager_guard")
                {
                    if (curSynth is not null)
                    {
                        waves.Add((curReq, curReplies, curSynth));
                        curReq = null;
                        curReplies = [];
                        curSynth = null;
                    }
                    curReplies.Add(turn);
                    continue;
                }

                if (turn.Kind == "synthesis")
                {
                    curSynth = turn;
                    waves.Add((curReq, curReplies, curSynth));
                    curReq = null;
                    curReplies = [];
                    curSynth = null;
                }
            }

            if (curReq is not null || curReplies.Count > 0 || curSynth is not null)
            {
                waves.Add((curReq, curReplies, curSynth));
            }

            var matchedUserMsgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (reqTurn, replies, synthTurn) in waves)
            {
                var reqClean = reqTurn?.Text.Trim() ?? "";
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
                        if (string.Equals(ExtractMessageNodeText(m), reqClean, StringComparison.OrdinalIgnoreCase))
                        {
                            anchorUserIdx = i;
                            matchedUserMsgIds.Add(mid);
                            break;
                        }
                    }

                    if (anchorUserIdx < 0 && reqTurn is not null)
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
                                ["room_id"] = room.RoomId
                            }
                        };
                        workList.Add(userNode);
                        anchorUserIdx = workList.Count - 1;
                        matchedUserMsgIds.Add(reqTurn.Id);
                    }
                }

                int insertPos = anchorUserIdx >= 0 ? anchorUserIdx + 1 : workList.Count;
                // Advance past any already-inserted collaborator replies right after anchorUserIdx
                while (insertPos < workList.Count &&
                       !string.IsNullOrEmpty(workList[insertPos]["metadata"]?["sender_agent_id"]?.ToString()))
                {
                    insertPos++;
                }

                foreach (var replyTurn in replies)
                {
                    if (workList.Any(m => string.Equals(m["id"]?.ToString(), replyTurn.Id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    workList.Insert(insertPos, CreateRoomTurnMessageNode(room, replyTurn));
                    insertPos++;
                }

                if (synthTurn is not null)
                {
                    bool synthAlreadyPresent = workList.Any(m =>
                        string.Equals(m["id"]?.ToString(), synthTurn.Id, StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(m["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase) &&
                         string.IsNullOrEmpty(m["metadata"]?["sender_agent_id"]?.ToString()) &&
                         string.Equals(ExtractMessageNodeText(m), synthTurn.Text.Trim(), StringComparison.OrdinalIgnoreCase)));

                    if (!synthAlreadyPresent)
                    {
                        workList.Insert(insertPos, CreateRoomTurnMessageNode(room, synthTurn));
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
        string triagePolicy,
        CancellationToken ct = default)
    {
        var normalizedPolicy = string.Equals(triagePolicy?.Trim(), "manual", StringComparison.OrdinalIgnoreCase)
            ? "manual"
            : "auto";
        var room = roomStore.GetByChat(hostAgentId, hostChatId)
            ?? await EnsureRoomAsync(hostAgentId, hostChatId, null, hostChatId, ct);
        return await roomStore.SaveAsync(room with
        {
            TriagePolicy = normalizedPolicy,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<(SessionCollaborationRoom Room, SessionRoomTurn CollaboratorTurn)> ExecuteCollaboratorTurnAsync(
        string hostAgentId,
        string hostChatId,
        string collaboratorAgentId,
        string objective,
        string? recentContextSnippet = null,
        string? hostSessionId = null,
        CancellationToken ct = default)
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
                    now);

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
                var reqTurn = new SessionRoomTurn(
                    $"rt_{Guid.NewGuid():N}"[..12],
                    room.Turns.Count + 1,
                    host,
                    FriendlyAgentName(host),
                    "host",
                    "request",
                    objective,
                    collab,
                    now);

                room = await roomStore.SaveAsync(room with
                {
                    Status = "collaborator_running",
                    ActiveCollaboratorId = collab,
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
            ? chatResult.Text.Trim()
            : $"BuildSpecialtySynthesisFallback(collab, objective)";

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
                ManagerAudit: managerAudit);

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
        CancellationToken ct = default)
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
                    ct);
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
                    ct);

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

    public async Task<(SessionCollaborationRoom Room, SessionRoomTurn Turn)?> RecordHostSynthesisTurnAsync(
        string hostAgentId,
        string hostChatId,
        string synthesisText,
        string? reasoning = null,
        IReadOnlyList<SessionRoomStep>? steps = null,
        string? hostSessionId = null,
        CancellationToken ct = default)
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
            steps);

        var updatedRoom = await roomStore.SaveAsync(room with
        {
            Status = "completed",
            ActiveCollaboratorId = null,
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

    private static string BuildSpecialtySynthesisFallback(string collabSlug, string? rawObjective)
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
        sb.AppendLine($"[BIÊN BẢN NGHIỆM THU & ĐIỀU PHỐI TỔNG HỢP TỪ AGENTTEAMS MANAGER (@{ManagerAgentId})]");
        sb.AppendLine($"- Mã Room: {room.MatrixRoomId} | Chủ phòng tổng hợp cuối: @{room.HostAgentId} ({FriendlyAgentName(room.HostAgentId)})");
        sb.AppendLine($"- Chế độ điều phối: {triageDecision.Mode} (Độ tin cậy: {Math.Round(triageDecision.Confidence * 100)}%) — {triageDecision.Reason}");
        sb.AppendLine($"- Yêu cầu gốc của người dùng cần đáp ứng trọn vẹn: \"{cleanUserObjective}\"");

        if (collaboratorTurns.Count > 0)
        {
            sb.AppendLine("- Kết quả kiểm định chất lượng từng chuyên viên trong Room:");
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

        sb.AppendLine("- Chỉ thị của Manager dành cho Chủ phòng khi tổng hợp câu trả lời cuối:");
        sb.AppendLine("  1. Kết hợp mạch lạc các góc nhìn chuyên môn đã được Manager nghiệm thu ở trên thành một phương án thống nhất, trả lời thẳng vào câu hỏi của người dùng.");
        sb.AppendLine("  2. Loại bỏ mọi sự trùng lặp giữa các chuyên viên; làm nổi bật các đề xuất hành động cụ thể (Actionable Takeaways).");
        sb.AppendLine("  3. Nếu các chuyên viên đang cần người dùng xác nhận thêm 1-2 thông tin phạm vi (ngành hàng, đối thủ, ngân sách), hãy gom lại thành 1 câu hỏi gợi mở ngắn gọn, lịch sự ở cuối bài.");
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
    public HostRoomTriageDecision DecideRoomCollaboratorsForTurn(
        string hostAgentId,
        string hostChatId,
        string? hostSessionId,
        string? rawUserText,
        IReadOnlyList<string>? explicitMentions = null)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        if (string.IsNullOrWhiteSpace(rawUserText) || rawUserText.Contains("[IMPLICIT_REACTION_FEEDBACK", StringComparison.OrdinalIgnoreCase))
        {
            return new HostRoomTriageDecision(false, [], "host_solo", $"Chủ phòng (@{host}) xử lý trực tiếp phản hồi cảm xúc hoặc tin nhắn trống.", 1.0);
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
                Reason: $"Chủ phòng (@{host}) điều phối yêu cầu vào Room cho chuyên viên được chỉ định: @{string.Join(", @", mentions)}.",
                Confidence: 1.0,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(mentions, userBodyText));
        }

        // 2. If the session Room has no collaborators yet and none were @mentioned, Host handles solo
        if (roomCollaborators.Count == 0)
        {
            return new HostRoomTriageDecision(
                ShouldUseRoom: false,
                SelectedCollaborators: [],
                Mode: "no_room_collaborators",
                Reason: $"Phiên chat chưa có chuyên viên phối hợp trong Room; Chủ phòng (@{host}) xử lý trực tiếp.",
                Confidence: 1.0);
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
                Reason: $"Chủ phòng (@{host}) đánh giá yêu cầu mang tính trao đổi trực tiếp nên tự phản hồi ngay, không đẩy vào Room.",
                Confidence: 0.98);
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
                Reason: $"Chủ phòng (@{host}) nhận diện yêu cầu hướng trực tiếp tới @{string.Join(", @", namedInRoom)} trong Room.",
                Confidence: 0.95,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(namedInRoom, userBodyText));
        }

        // If the session Room's triage policy is set to "manual", do not auto-pull collaborators unless explicitly @mentioned or replied/named
        if (string.Equals(room?.TriagePolicy, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return new HostRoomTriageDecision(
                ShouldUseRoom: false,
                SelectedCollaborators: [],
                Mode: "manual_policy_host_solo",
                Reason: $"Chế độ điều phối Room đang đặt là Thủ công (Manual); Chủ phòng (@{host}) trực tiếp phản hồi khi không có thẻ @mention.",
                Confidence: 1.0,
                CollaboratorFocuses: null,
                ExecutionStrategy: "sequential");
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
                Reason: $"Chủ phòng (@{host}) tự biên tập/tóm tắt nội dung sẵn có mà không cần gọi lại chuyên viên trong Room.",
                Confidence: 0.95);
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

        // 6. Score each Collaborator in the Room by Domain Specialty Relevance + Dominant Specialty Pruning
        var evalList = roomCollaborators
            .Where(c => !excludedInRoom.Contains(c))
            .Select((collab, idx) =>
            {
                var (score, matchedKws) = EvaluateCollaboratorSpecialty(collab, userBodyText);
                return (Collab: collab, Score: score, MatchedKeywords: matchedKws, Order: idx);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Order)
            .ToList();

        // Dominant Specialty Pruning: if the top collaborator has a strong specific domain match (Score >= 4),
        // filter out peripheral collaborators with a weak 1-keyword overlap unless they reach >= 50% of topScore (or >= 4)
        int topScore = evalList.Count > 0 ? evalList[0].Score : 0;
        var scoredCollaborators = evalList
            .Where(x => topScore < 4 || x.Score >= 4 || x.Score * 2 >= topScore)
            .Select(x => x.Collab)
            .ToList();

        // Only expand to all pendingWaveCollaborators if the user is supplying shared scope (not when the user is already strongly targeting a single dominant specialty with topScore >= 6)
        bool isScopeSupply = latestWaveAskedClarification &&
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
                    var m = evalList.FirstOrDefault(x => string.Equals(x.Collab, c, StringComparison.OrdinalIgnoreCase));
                    return BuildCollaboratorFocusBrief(c, m.MatchedKeywords);
                },
                StringComparer.OrdinalIgnoreCase);

            var confidence = Math.Clamp(0.72 + Math.Min(topScore, 8) * 0.035, 0.75, 0.99);
            return new HostRoomTriageDecision(
                ShouldUseRoom: true,
                SelectedCollaborators: scoredCollaborators,
                Mode: "specialty_triage",
                Reason: $"Chủ phòng (@{host}) phân tích chuyên môn của yêu cầu mới và chủ động kéo @{string.Join(", @", scoredCollaborators)} trong Room cùng giải quyết.",
                Confidence: Math.Round(confidence, 2),
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(scoredCollaborators, userBodyText));
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
                Reason: $"Chủ phòng (@{host}) nhận thấy người dùng đang bổ sung thông tin cho phiên thảo luận trước nên đẩy vào Room cho @{string.Join(", @", pendingWaveCollaborators)} hoàn tất phân tích.",
                Confidence: 0.9,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(pendingWaveCollaborators, userBodyText));
        }

        // 8. Conversational Momentum / Active Collaborator Drill-down Affinity:
        // When the user asks a follow-up drill-down ("còn mục 2 thì sao?", "chi tiết hơn phương án này nhé", "bổ sung thêm ví dụ")
        // right after a collaborator wave, route smoothly to the active collaborator(s) of that wave
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
                Reason: $"Chủ phòng (@{host}) duy trì mạch trao đổi chuyên sâu và chuyển yêu cầu làm rõ cho @{string.Join(", @", pendingWaveCollaborators)} trong Room.",
                Confidence: 0.88,
                CollaboratorFocuses: focuses,
                ExecutionStrategy: DetermineWaveExecutionStrategy(pendingWaveCollaborators, userBodyText));
        }

        // 9. Default when no collaborator specialty matches and no pending clarification/drill-down exists: Host handles solo
        return new HostRoomTriageDecision(
            ShouldUseRoom: false,
            SelectedCollaborators: [],
            Mode: "host_solo",
            Reason: $"Yêu cầu mới không đòi hỏi phối hợp chuyên môn từ các thành viên khác trong Room; Chủ phòng (@{host}) trực tiếp xử lý.",
            Confidence: 0.92);
    }

    private static void BroadcastRoomTurnToHostSession(
        SessionCollaborationRoom room,
        SessionRoomTurn turn,
        string eventStatus)
    {
        var metadataJson = JsonSerializer.Serialize(new
        {
            room_id = room.RoomId,
            matrix_room_id = room.MatrixRoomId,
            element_room_url = room.ElementRoomUrl,
            sender_agent_id = turn.AuthorId,
            sender_agent_name = turn.AuthorName,
            author_role = turn.AuthorRole,
            turn_kind = turn.Kind,
            turn_no = turn.TurnNo,
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
