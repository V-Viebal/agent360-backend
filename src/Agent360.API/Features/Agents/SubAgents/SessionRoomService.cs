using System.Collections.Concurrent;
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

public sealed record SessionRoomTurn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("turn_no")] int TurnNo,
    [property: JsonPropertyName("author_id")] string AuthorId,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("author_role")] string AuthorRole, // "host" | "collaborator" | "manager" | "admin" | "user"
    [property: JsonPropertyName("kind")] string Kind, // "request" | "collaborator_progress" | "collaborator_reply" | "question" | "answer" | "manager_guard" | "synthesis"
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("target_agent_id")] string? TargetAgentId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

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
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record PullAgentIntoRoomRequest(
    [property: JsonPropertyName("collaborator_agent_id")] string CollaboratorAgentId,
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

public sealed class SessionRoomStore
{
    private readonly ConcurrentDictionary<string, SessionCollaborationRoom> _roomsByChatKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _chatKeyByRoomId = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storageFile;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SessionRoomStore(IHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "_storage", "subagents");
        Directory.CreateDirectory(dir);
        _storageFile = Path.Combine(dir, "session_rooms.json");
        LoadFromDisk();
    }

    public SessionRoomStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agent360_rooms_test");
        Directory.CreateDirectory(dir);
        _storageFile = Path.Combine(dir, "session_rooms.json");
    }

    private static string MakeKey(string hostAgentId, string hostChatId) =>
        $"{SubAgentService.NormalizeAgentId(hostAgentId).ToLowerInvariant()}:{hostChatId.Trim()}";

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storageFile)) return;
            var json = File.ReadAllText(_storageFile);
            var list = JsonSerializer.Deserialize<List<SessionCollaborationRoom>>(json);
            if (list is null) return;
            foreach (var r in list)
            {
                var key = MakeKey(r.HostAgentId, r.HostChatId);
                _roomsByChatKey[key] = r;
                _chatKeyByRoomId[r.RoomId] = key;
                _chatKeyByRoomId[r.MatrixRoomId] = key;
            }
        }
        catch {}
    }

    private async Task PersistAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(_roomsByChatKey.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storageFile, json);
        }
        catch {}
        finally
        {
            _saveLock.Release();
        }
    }

    public SessionCollaborationRoom? GetByChat(string hostAgentId, string hostChatId)
    {
        if (_roomsByChatKey.TryGetValue(MakeKey(hostAgentId, hostChatId), out var room))
            return room;
        // Fallback: lookup by hostSessionId if chatId is a session_id
        return _roomsByChatKey.Values.FirstOrDefault(r =>
            string.Equals(r.HostAgentId, SubAgentService.NormalizeAgentId(hostAgentId), StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.HostChatId, hostChatId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.HostSessionId, hostChatId, StringComparison.OrdinalIgnoreCase)));
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
        return _roomsByChatKey.Values
            .Where(r => string.Equals(r.HostAgentId, norm, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.UpdatedAt)
            .ToList();
    }

    public string EnrichChatHistoryJson(string historyRaw, string? agentId, string chatId)
    {
        try
        {
            var room = GetByChat(agentId ?? "mons", chatId);
            if (room is null) return historyRaw;

            var node = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(historyRaw) ? "{}" : historyRaw) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();

            node["collaboration_room"] = JsonSerializer.SerializeToNode(room);

            if (node["messages"] is not System.Text.Json.Nodes.JsonArray messagesArr)
            {
                messagesArr = new System.Text.Json.Nodes.JsonArray();
                node["messages"] = messagesArr;
            }

            foreach (var turn in room.Turns.Where(t => t.Kind is "collaborator_reply" or "question" or "manager_guard"))
            {
                var alreadyPresent = messagesArr.Any(m => m?["id"]?.ToString() == turn.Id);
                if (alreadyPresent) continue;

                var msgObj = new System.Text.Json.Nodes.JsonObject
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
                        ["sender_agent_id"] = turn.AuthorId,
                        ["sender_agent_name"] = turn.AuthorName,
                        ["author_role"] = turn.AuthorRole,
                        ["turn_kind"] = turn.Kind,
                        ["room_id"] = room.RoomId,
                        ["matrix_room_id"] = room.MatrixRoomId,
                        ["element_room_url"] = room.ElementRoomUrl
                    }
                };
                messagesArr.Add(msgObj);
            }

            return node.ToJsonString();
        }
        catch
        {
            return historyRaw;
        }
    }

    public async Task<SessionCollaborationRoom> SaveAsync(SessionCollaborationRoom room)
    {
        var key = MakeKey(room.HostAgentId, room.HostChatId);
        _roomsByChatKey[key] = room;
        _chatKeyByRoomId[room.RoomId] = key;
        _chatKeyByRoomId[room.MatrixRoomId] = key;
        await PersistAsync();
        return room;
    }
}

public sealed partial class SessionRoomOrchestrator(
    SessionRoomStore roomStore,
    SubAgentStore subAgentStore,
    IQwenPawA2AClient a2aClient,
    SessionQueueManager queueManager,
    ILogger<SessionRoomOrchestrator> logger)
{
    public const string GatewayDomain = "agentteams-gateway.hmz.one";
    public const string ElementBaseUrl = "https://chat.hmz.one/#/room/";
    public const string ManagerAgentId = "agentteams-manager";
    public const string AdminUserId = "admin";

    private static string MatrixId(string id) => $"@{SubAgentService.NormalizeAgentId(id).ToLowerInvariant()}:{GatewayDomain}";

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

    /// <summary>
    /// Ensures a Session-Sticky Room exists for (hostAgentId, hostChatId) with the 4 canonical roles:
    /// Host Agent A, Collaborator Agent B, AgentTeams Manager, and Admin.
    /// Also performs Zero-Friction Binding if A -> B is not bound yet.
    /// </summary>
    public async Task<SessionCollaborationRoom> EnsureRoomAsync(
        string hostAgentId,
        string hostChatId,
        string collaboratorAgentId,
        string? hostSessionId = null,
        CancellationToken ct = default)
    {
        var host = SubAgentService.NormalizeAgentId(hostAgentId);
        var collab = SubAgentService.NormalizeAgentId(collaboratorAgentId);
        var now = DateTimeOffset.UtcNow;

        // 1. Zero-Friction Pull: Ensure SubAgentBinding exists between Host A and Collaborator B
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

        // 2. Check if Room already exists for this chat session
        var existingRoom = roomStore.GetByChat(host, hostChatId);
        if (existingRoom is not null)
        {
            var participants = existingRoom.Participants.ToList();
            if (participants.All(p => !string.Equals(p.AgentId, collab, StringComparison.OrdinalIgnoreCase)))
            {
                participants.Add(new SessionRoomParticipant(
                    collab,
                    FriendlyAgentName(collab),
                    "collaborator",
                    MatrixId(collab),
                    "idle",
                    now));
            }
            var updated = existingRoom with
            {
                HostSessionId = !string.IsNullOrWhiteSpace(hostSessionId) ? hostSessionId : existingRoom.HostSessionId,
                Participants = participants,
                UpdatedAt = now
            };
            return await roomStore.SaveAsync(updated);
        }

        // 3. Create new Session-Sticky AgentTeams Room with 4 canonical members: [A, B, manager, admin]
        var shortHash = Math.Abs($"{host}:{hostChatId}".GetHashCode()).ToString("x8");
        var roomSlug = $"room_{host}_{shortHash}";
        var matrixRoomId = $"!{roomSlug}:{GatewayDomain}";
        var elementUrl = $"{ElementBaseUrl}{Uri.EscapeDataString(matrixRoomId)}";

        var canonicalParticipants = new List<SessionRoomParticipant>
        {
            new(host, FriendlyAgentName(host), "host", MatrixId(host), "idle", now),
            new(collab, FriendlyAgentName(collab), "collaborator", MatrixId(collab), "idle", now),
            new(ManagerAgentId, "AgentTeams Manager", "manager", MatrixId("manager"), "idle", now),
            new(AdminUserId, "Admin Operator", "admin", MatrixId("admin"), "idle", now)
        };

        var systemInitTurn = new SessionRoomTurn(
            $"rt_{Guid.NewGuid():N}"[..12],
            1,
            ManagerAgentId,
            "AgentTeams Manager",
            "manager",
            "request",
            $"Room [{matrixRoomId}] được khởi tạo gắn với phiên chat '{hostChatId}' của {FriendlyAgentName(host)}. Thành viên: {FriendlyAgentName(host)} (Host), {FriendlyAgentName(collab)} (Collaborator), AgentTeams Manager, Admin.",
            collab,
            now);

        var room = new SessionCollaborationRoom(
            RoomId: roomSlug,
            MatrixRoomId: matrixRoomId,
            ElementRoomUrl: elementUrl,
            HostAgentId: host,
            HostChatId: hostChatId,
            HostSessionId: hostSessionId ?? hostChatId,
            Participants: canonicalParticipants,
            Status: "idle",
            ActiveCollaboratorId: collab,
            MaxRoomTurns: 4,
            CurrentTurnCount: 1,
            Turns: [systemInitTurn],
            CreatedAt: now,
            UpdatedAt: now);

        return await roomStore.SaveAsync(room);
    }

    /// <summary>
    /// Executes a collaborative turn with Agent B inside the Session-Sticky Room and projects B's response
    /// directly back onto Agent A's chat session. Returns B's reply text so Agent A can synthesize the final answer.
    /// </summary>
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
        var now = DateTimeOffset.UtcNow;

        // 1. Check Manager Ping-Pong Loop Guard
        var collabTurnsInRoom = room.Turns.Count(t => string.Equals(t.AuthorId, collab, StringComparison.OrdinalIgnoreCase));
        if (collabTurnsInRoom >= room.MaxRoomTurns)
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
            return (guardedRoom, guardTurn);
        }

        // 2. Record Request Turn from Host/User to Collaborator B in Room
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

        var runningParticipants = room.Participants.Select(p =>
            string.Equals(p.AgentId, collab, StringComparison.OrdinalIgnoreCase)
                ? p with { Status = "running" }
                : p).ToList();

        room = await roomStore.SaveAsync(room with
        {
            Status = "collaborator_running",
            ActiveCollaboratorId = collab,
            Participants = runningParticipants,
            CurrentTurnCount = room.Turns.Count + 1,
            Turns = [.. room.Turns, reqTurn],
            UpdatedAt = now
        });

        // Mark host session active while collaborator B works in the room
        queueManager.SetSessionTurnActive(room.HostChatId, true);
        if (!string.Equals(room.HostSessionId, room.HostChatId, StringComparison.OrdinalIgnoreCase))
        {
            queueManager.SetSessionTurnActive(room.HostSessionId, true);
        }

        BroadcastRoomTurnToHostSession(room, reqTurn, "in_progress");

        // 3. Build Scoped Context Injection Prompt for Agent B (with Room Routing Metadata)
        var scopedPrompt = BuildRoomScopedPrompt(room, collab, objective, recentContextSnippet);
        var roomChildSessionId = $"room:{room.RoomId}:{host}:to:{collab}";

        NativeChatResult chatResult;
        try
        {
            // Detach from browser request cancellation so reload/tab-switch never aborts the room turn
            chatResult = await a2aClient.ChatInSessionAsync(host, collab, scopedPrompt, roomChildSessionId, 45, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Collaborator {Collab} execution in room {RoomId} failed", collab, room.RoomId);
            chatResult = new NativeChatResult(false, roomChildSessionId, null, ex.Message, 502);
        }

        var finishNow = DateTimeOffset.UtcNow;
        var replyText = chatResult.Success && !string.IsNullOrWhiteSpace(chatResult.Text)
            ? chatResult.Text.Trim()
            : $"[Thông báo từ Room {room.MatrixRoomId}] Chuyên viên @{collab} hiện đang bận hoặc không phản hồi kịp thời ({chatResult.Error ?? "timeout"}). Host @{host} sẽ tiếp tục xử lý trực tiếp.";

        // 4. Check if Collaborator B asked a clarification question (QUESTION_FOR_PARENT)
        var question = chatResult.Success ? SubAgentPolicy.ExtractQuestion(replyText) : null;
        var replyKind = question is not null ? "question" : "collaborator_reply";
        var nextRoomStatus = question is not null ? "awaiting_clarification" : "completed";
        var nextCollabStatus = question is not null ? "awaiting_input" : "completed";

        var collabTurn = new SessionRoomTurn(
            $"rt_{Guid.NewGuid():N}"[..12],
            room.Turns.Count + 1,
            collab,
            FriendlyAgentName(collab),
            "collaborator",
            replyKind,
            replyText,
            host,
            finishNow);

        var finishedParticipants = room.Participants.Select(p =>
            string.Equals(p.AgentId, collab, StringComparison.OrdinalIgnoreCase)
                ? p with { Status = nextCollabStatus }
                : p).ToList();

        var completedRoom = await roomStore.SaveAsync(room with
        {
            Status = nextRoomStatus,
            ActiveCollaboratorId = question is not null ? collab : null,
            Participants = finishedParticipants,
            CurrentTurnCount = room.Turns.Count + 1,
            Turns = [.. room.Turns, collabTurn],
            UpdatedAt = finishNow
        });

        // 5. Project Collaborator B's reply onto Host Session A via ChannelResponseStore
        BroadcastRoomTurnToHostSession(completedRoom, collabTurn, "completed");

        return (completedRoom, collabTurn);
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
        var room = roomStore.GetByChat(hostAgentId, hostChatId)
            ?? await EnsureRoomAsync(hostAgentId, hostChatId, author, null, ct);

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
        return (updatedRoom, turn);
    }

    /// <summary>
    /// Detects if a user message mentions another agent (e.g. @zen, @ken, @gm-boc, or @Zen)
    /// and resolves the mentioned collaborator agent ID.
    /// </summary>
    public static string? DetectMentionedCollaborator(string? messageText, string hostAgentId, IEnumerable<string>? knownAgentSlugs = null)
    {
        if (string.IsNullOrWhiteSpace(messageText)) return null;
        var normHost = SubAgentService.NormalizeAgentId(hostAgentId).ToLowerInvariant();

        var defaultSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mons", "ken", "zen", "mkai", "gm-boc", "gm-gao", "gm-lua", "planthy-pm", "climax-default", "climax-team"
        };
        if (knownAgentSlugs is not null)
        {
            foreach (var s in knownAgentSlugs)
                defaultSlugs.Add(SubAgentService.NormalizeAgentId(s));
        }

        foreach (Match m in MentionRegex().Matches(messageText))
        {
            var candidate = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(candidate) || candidate == normHost) continue;
            if (defaultSlugs.Contains(candidate)) return candidate;
            // Match prefix (e.g. @gm-boc or @planthy)
            var matched = defaultSlugs.FirstOrDefault(s => s.Equals(candidate, StringComparison.OrdinalIgnoreCase) || s.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase));
            if (matched is not null && !string.Equals(matched, normHost, StringComparison.OrdinalIgnoreCase))
            {
                return matched;
            }
        }
        return null;
    }

    private static string BuildRoomScopedPrompt(
        SessionCollaborationRoom room,
        string collaboratorId,
        string objective,
        string? recentContextSnippet)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[AGENTTEAMS COLLABORATION ROOM: {room.MatrixRoomId}]");
        sb.AppendLine($"- Host Agent (Owner): @{room.HostAgentId} (Chat Session: {room.HostChatId})");
        sb.AppendLine($"- Collaborator Agent (You): @{collaboratorId}");
        sb.AppendLine($"- Room Governance: @{ManagerAgentId} (Manager), @{AdminUserId} (Admin)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(recentContextSnippet))
        {
            sb.AppendLine("## Ngữ cảnh hội thoại gần đây từ phiên chat của Host Agent:");
            sb.AppendLine(recentContextSnippet.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## Yêu cầu cần bạn xử lý và phản hồi về phiên chat của Host Agent:");
        sb.AppendLine(objective.Trim());
        sb.AppendLine();
        sb.AppendLine("Lưu ý: Phản hồi ngắn gọn, đi thẳng vào trọng tâm chuyên môn của bạn để được chuyển tiếp trực tiếp về Chat Session của Host Agent và tổng hợp cho người dùng. Nếu thiếu thông tin bắt buộc, hãy bắt đầu dòng đầu bằng `QUESTION_FOR_PARENT: <câu hỏi>`.");
        return sb.ToString();
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
            target_agent_id = turn.TargetAgentId
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
