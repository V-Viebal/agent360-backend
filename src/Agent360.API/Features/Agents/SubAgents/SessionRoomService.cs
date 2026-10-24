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
    [property: JsonPropertyName("steps")] IReadOnlyList<SessionRoomStep>? Steps = null);

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
    [property: JsonPropertyName("room_title")] string? RoomTitle = null);

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
                if (!string.IsNullOrWhiteSpace(r.HostSessionId))
                {
                    _roomsByChatKey[MakeKey(r.HostAgentId, r.HostSessionId)] = r;
                }
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
            var distinctRooms = _roomsByChatKey.Values
                .GroupBy(r => r.RoomId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.UpdatedAt).First())
                .ToList();
            var json = JsonSerializer.Serialize(distinctRooms, new JsonSerializerOptions { WriteIndented = true });
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
        if (string.IsNullOrWhiteSpace(hostChatId)) return null;
        if (_roomsByChatKey.TryGetValue(MakeKey(hostAgentId, hostChatId), out var room))
            return room;
        return _roomsByChatKey.Values.FirstOrDefault(r =>
            string.Equals(r.HostAgentId, SubAgentService.NormalizeAgentId(hostAgentId), StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.HostChatId, hostChatId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.HostSessionId, hostChatId, StringComparison.OrdinalIgnoreCase)));
    }

    public void BindSessionAlias(string hostAgentId, string chatId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(sessionId)) return;
        var existing = GetByChat(hostAgentId, chatId) ?? GetByChat(hostAgentId, sessionId);
        if (existing is null) return;
        var updated = existing with
        {
            HostChatId = !chatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase) ? chatId : existing.HostChatId,
            HostSessionId = sessionId
        };
        _roomsByChatKey[MakeKey(hostAgentId, chatId)] = updated;
        _roomsByChatKey[MakeKey(hostAgentId, sessionId)] = updated;
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
            .GroupBy(r => r.RoomId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.UpdatedAt).First())
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

            // Strip internal [SHARED SESSION ROOM CONTEXT] synthesis block from persisted user messages so the UI only shows the user's clean message
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

            // Project room turns in chronological order (User Request -> Collaborator Replies -> Host Synthesis LAST)
            foreach (var turn in room.Turns.OrderBy(t => t.TurnNo))
            {
                if (turn.Kind == "request" && !string.Equals(turn.AuthorRole, "manager", StringComparison.OrdinalIgnoreCase))
                {
                    var reqClean = turn.Text.Trim();
                    var reqExists = messagesArr.Any(m =>
                    {
                        if (!string.Equals(m?["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase)) return false;
                        var mText = m?["content"] is System.Text.Json.Nodes.JsonArray arr
                            ? string.Join("\n", arr.Select(x => x?["text"]?.ToString() ?? ""))
                            : m?["content"]?.ToString() ?? "";
                        return mText.Trim().Equals(reqClean, StringComparison.OrdinalIgnoreCase);
                    });
                    if (!reqExists && !string.IsNullOrWhiteSpace(reqClean))
                    {
                        messagesArr.Add(new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = turn.Id,
                            ["role"] = "user",
                            ["type"] = "message",
                            ["status"] = "completed",
                            ["content"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = reqClean }),
                            ["metadata"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["timestamp"] = turn.CreatedAt.ToString("O"),
                                ["room_id"] = room.RoomId
                            }
                        });
                    }
                    continue;
                }

                if (turn.Kind is not ("collaborator_reply" or "question" or "manager_guard" or "synthesis"))
                    continue;

                var isSynthesis = turn.Kind == "synthesis";
                var alreadyPresent = messagesArr.Any(m =>
                    m?["id"]?.ToString() == turn.Id ||
                    (isSynthesis && string.Equals(m?["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase) &&
                     (m?["content"] is System.Text.Json.Nodes.JsonArray cArr
                        ? string.Join("\n", cArr.Select(x => x?["text"]?.ToString() ?? "")).Trim() == turn.Text.Trim()
                        : (m?["content"]?.ToString() ?? "").Trim() == turn.Text.Trim())));
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
                        ["sender_agent_id"] = isSynthesis ? null : turn.AuthorId,
                        ["sender_agent_name"] = isSynthesis ? null : turn.AuthorName,
                        ["author_role"] = turn.AuthorRole,
                        ["turn_kind"] = turn.Kind,
                        ["room_id"] = room.RoomId,
                        ["matrix_room_id"] = room.MatrixRoomId,
                        ["element_room_url"] = room.ElementRoomUrl,
                        ["reasoning"] = turn.Reasoning,
                        ["steps"] = turn.Steps is not null ? JsonSerializer.SerializeToNode(turn.Steps) : null
                    }
                };
                messagesArr.Add(msgObj);
            }

            // Guarantee chronological order and ensure Host Synthesis comes AFTER all Collaborator Replies for the same turn batch
            var sortedMessages = messagesArr
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .Select((m, idx) =>
                {
                    var tsStr = m["metadata"]?["timestamp"]?.ToString() ?? m["created_at"]?.ToString();
                    var parsedTs = DateTimeOffset.TryParse(tsStr, out var dto) ? dto : DateTimeOffset.MinValue;
                    var role = m["role"]?.ToString() ?? "assistant";
                    var turnKind = m["metadata"]?["turn_kind"]?.ToString();
                    var senderAg = m["metadata"]?["sender_agent_id"]?.ToString();
                    // Within the same collaboration window: user (0) -> collaborator_reply (1) -> host synthesis / assistant (2)
                    var phaseOrder = role == "user" ? 0 : (!string.IsNullOrEmpty(senderAg) && turnKind != "synthesis" ? 1 : 2);
                    return (Node: System.Text.Json.Nodes.JsonNode.Parse(m.ToJsonString())!, Ts: parsedTs, Phase: phaseOrder, Idx: idx);
                })
                .OrderBy(x => x.Ts == DateTimeOffset.MinValue ? 1 : 0)
                .ThenBy(x => x.Ts)
                .ThenBy(x => x.Phase)
                .ThenBy(x => x.Idx)
                .Select(x => x.Node)
                .ToArray();

            node["messages"] = new System.Text.Json.Nodes.JsonArray(sortedMessages);

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
        if (!string.IsNullOrWhiteSpace(room.HostSessionId))
        {
            _roomsByChatKey[MakeKey(room.HostAgentId, room.HostSessionId)] = room;
        }
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
            .Append($"@mon:{GatewayDomain}")
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
            using var _ = await SharedMatrixHttp.SendAsync(req);
        }
        catch {}
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

        // 2. Check if Room already exists for this chat session
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

            var updated = existingRoom with
            {
                MatrixRoomId = matrixRoomId,
                ElementRoomUrl = $"{ElementBaseUrl}{Uri.EscapeDataString(matrixRoomId)}",
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
            _ = PostTurnToRealMatrixRoomAsync(guardedRoom, guardTurn);
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

        queueManager.SetSessionTurnActive(room.HostChatId, true);
        if (!string.Equals(room.HostSessionId, room.HostChatId, StringComparison.OrdinalIgnoreCase))
        {
            queueManager.SetSessionTurnActive(room.HostSessionId, true);
        }

        BroadcastRoomTurnToHostSession(room, reqTurn, "in_progress");
        _ = PostTurnToRealMatrixRoomAsync(room, reqTurn);

        // 3. Build Scoped Context Injection Prompt for Agent B (with Room Routing Metadata)
        var scopedPrompt = BuildRoomScopedPrompt(room, collab, objective, recentContextSnippet);
        var roomChildSessionId = $"room:{room.RoomId}:{host}:to:{collab}";

        NativeChatResult chatResult;
        try
        {
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
            : $"Chuyên viên @{collab} hiện đang bận hoặc không phản hồi kịp thời ({chatResult.Error ?? "timeout"}). Host @{host} sẽ tiếp tục xử lý trực tiếp.";

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
            finishNow,
            Reasoning: $"Thực thi nhiệm vụ phối hợp từ @{host} trong phiên {room.HostChatId}.",
            Steps:
            [
                new SessionRoomStep(
                    Kind: "reasoning",
                    Id: $"step_r_{Guid.NewGuid():N}"[..10],
                    Text: $"Phân tích yêu cầu từ @{host} và ngữ cảnh chung của phiên chat '{room.HostChatId}'."),
                new SessionRoomStep(
                    Kind: "tool_call",
                    Id: $"step_t_{Guid.NewGuid():N}"[..10],
                    CallId: $"call_{Guid.NewGuid():N}"[..10],
                    Tool: "session_room_collaborate",
                    Args: JsonSerializer.Serialize(new { room_id = room.MatrixRoomId, host_agent = host, collaborator = collab, session = room.HostChatId }),
                    Result: chatResult.Success ? "completed" : (chatResult.Error ?? "fallback"),
                    State: chatResult.Success ? "success" : "error")
            ]);

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

        BroadcastRoomTurnToHostSession(completedRoom, collabTurn, "completed");
        _ = PostTurnToRealMatrixRoomAsync(completedRoom, collabTurn);

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

    private static string BuildRoomScopedPrompt(
        SessionCollaborationRoom room,
        string collaboratorId,
        string objective,
        string? recentContextSnippet)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[SESSION COLLABORATION CONTEXT: {room.HostChatId}]");
        sb.AppendLine($"- Host Agent: @{room.HostAgentId}");
        sb.AppendLine($"- Collaborator Agent (You): @{collaboratorId}");
        sb.AppendLine($"- Governance: @{ManagerAgentId} (Manager), @{AdminUserId} (Admin)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(recentContextSnippet))
        {
            sb.AppendLine("## Ngữ cảnh hội thoại trước đó trong phiên:");
            sb.AppendLine(recentContextSnippet.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## Yêu cầu cần bạn xử lý và phản hồi:");
        sb.AppendLine(objective.Trim());
        sb.AppendLine();
        sb.AppendLine("Lưu ý: Phản hồi ngắn gọn, đi thẳng vào trọng tâm chuyên môn của bạn để hiển thị trực tiếp trên phiên chat và tổng hợp cho người dùng. Nếu thiếu thông tin bắt buộc, hãy bắt đầu dòng đầu bằng `QUESTION_FOR_PARENT: <câu hỏi>`.");
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
            target_agent_id = turn.TargetAgentId,
            reasoning = turn.Reasoning,
            steps = turn.Steps
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
