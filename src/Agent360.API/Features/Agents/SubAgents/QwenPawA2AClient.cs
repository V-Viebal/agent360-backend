using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.SubAgents;

public interface IQwenPawA2AClient
{
    Task<IReadOnlyList<NativeAgentInfo>> ListAgentsAsync(CancellationToken ct);
    Task<NativeChatResult> ChatAsync(string fromAgent, string toAgent, string text, int timeoutSeconds, CancellationToken ct);
    /// <summary>Continue (or start, when <paramref name="sessionId"/> is null) a native chat session with the child.</summary>
    Task<NativeChatResult> ChatInSessionAsync(string fromAgent, string toAgent, string text, string? sessionId, int timeoutSeconds, CancellationToken ct);
    Task<NativeSubmitResult> SubmitAsync(string fromAgent, string toAgent, string text, int timeoutSeconds, CancellationToken ct);
    Task<NativeTaskStatus> GetTaskAsync(string nativeTaskId, string? toAgent, CancellationToken ct);
    Task<bool> StopChatAsync(string sessionId, string toAgent, CancellationToken ct);
    Task<bool> UpsertSkillAsync(string agentId, string skillName, string content, CancellationToken ct);
    Task<IReadOnlyList<DetectedNativeDelegation>> ScanAgentDelegationsAsync(string agentId, int maxChats, CancellationToken ct);
}

/// <summary>
/// Adapter over QwenPaw native inter-agent endpoints used by the
/// chat_with_agent / submit_to_agent / check_agent_task tools.
/// </summary>
public sealed partial class QwenPawA2AClient : IQwenPawA2AClient
{
    private const string AgentHeader = "X-Agent-Id";
    private const string AgentsCacheKey = "agent360:a2a:native-agents";
    private readonly HttpClient _http;
    private readonly IMemoryCache? _cache;
    private readonly IFleetInstanceRoutingService? _routingService;

    public QwenPawA2AClient(HttpClient http, IOptions<QwenPawOptions> options, IMemoryCache? cache = null, IFleetInstanceRoutingService? routingService = null)
    {
        _http = http;
        _cache = cache;
        _routingService = routingService;
        // Default fallback base address for unit tests that do not supply IFleetInstanceRoutingService
        options.Value.Instances.TryGetValue("core", out var core);
        var baseUrl = !string.IsNullOrWhiteSpace(core?.InternalUrl) ? core.InternalUrl : "http://100.76.185.111:33148";
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public static string BuildSessionId(string from, string to) =>
        $"{from}:to:{to}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Guid.NewGuid().ToString("N")[..8]}";

    public static string PrefixIdentity(string from, string text)
    {
        var trimmed = text.TrimStart();
        return AgentPrefixRegex().IsMatch(trimmed) ? text : $"[Agent {from} requesting] {text}";
    }

    public static object BuildPayload(string from, string sessionId, string text, int? timeout) => timeout is null
        ? new
        {
            session_id = sessionId,
            user_id = from,
            input = new[] { new { role = "user", content = new[] { new { type = "text", text = PrefixIdentity(from, text) } } } },
            request_context = new { root_agent_id = from }
        }
        : new
        {
            session_id = sessionId,
            user_id = from,
            input = new[] { new { role = "user", content = new[] { new { type = "text", text = PrefixIdentity(from, text) } } } },
            request_context = new { root_agent_id = from },
            timeout = timeout.Value
        };

    public async Task<IReadOnlyList<NativeAgentInfo>> ListAgentsAsync(CancellationToken ct)
    {
        // Agent inventory changes rarely; a short fresh cache keeps bind/health off the runtime's hot path,
        // and a last-good copy covers moments when the runtime is overloaded.
        if (_cache is not null && _cache.TryGetValue(AgentsCacheKey, out IReadOnlyList<NativeAgentInfo>? fresh) && fresh is { Count: > 0 })
        {
            return fresh;
        }
        try
        {
            var list = await LoadAgentsAsync(ct);
            if (_cache is not null && list.Count > 0)
            {
                _cache.Set(AgentsCacheKey, list, TimeSpan.FromSeconds(60));
                _cache.Set(AgentsCacheKey + ":last-good", list, TimeSpan.FromMinutes(10));
            }
            return list;
        }
        catch (QwenPawUnavailableException) when (_cache is not null && _cache.TryGetValue(AgentsCacheKey + ":last-good", out IReadOnlyList<NativeAgentInfo>? stale) && stale is { Count: > 0 })
        {
            return stale;
        }
    }

    private async Task<IReadOnlyList<NativeAgentInfo>> LoadAgentsAsync(CancellationToken ct)
    {
        string body;
        try
        {
            using var cts = Linked(ct, 45);
            body = await _http.GetStringAsync("api/agents", cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException)
        {
            // Read-only call: one bounded retry when the runtime is momentarily busy.
            using var retry = Linked(ct, 45);
            try
            {
                body = await _http.GetStringAsync("api/agents", retry.Token);
            }
            catch (Exception inner) when (!ct.IsCancellationRequested && inner is OperationCanceledException or HttpRequestException)
            {
                throw new QwenPawUnavailableException("AgentCore /api/agents did not respond", inner);
            }
        }

        using var doc = JsonDocument.Parse(body);
        var list = new List<NativeAgentInfo>();
        if (doc.RootElement.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in agents.EnumerateArray())
            {
                var id = Str(a, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new NativeAgentInfo(id, Str(a, "name") ?? id, !a.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False));
            }
        }
        return list;
    }

    public Task<NativeChatResult> ChatAsync(string fromAgent, string toAgent, string text, int timeoutSeconds, CancellationToken ct) =>
        ChatInSessionAsync(fromAgent, toAgent, text, null, timeoutSeconds, ct);

    public async Task<NativeChatResult> ChatInSessionAsync(string fromAgent, string toAgent, string text, string? existingSessionId, int timeoutSeconds, CancellationToken ct)
    {
        var sessionId = string.IsNullOrWhiteSpace(existingSessionId) ? BuildSessionId(fromAgent, toAgent) : existingSessionId;
        using var cts = Linked(ct, timeoutSeconds);
        var resolvedTarget = _routingService?.ResolveTarget(toAgent);
        var effectiveAgentProfile = resolvedTarget?.TargetAgentProfile ?? toAgent;
        try
        {
            HttpResponseMessage first;
            if (_routingService is not null && resolvedTarget?.Instance is not null)
            {
                first = await _routingService.SendAsync(
                    resolvedTarget.Instance,
                    HttpMethod.Post,
                    $"agents/{Uri.EscapeDataString(effectiveAgentProfile)}/console/chat",
                    () => JsonContent.Create(BuildPayload(fromAgent, sessionId, text, null)),
                    effectiveAgentProfile,
                    TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)),
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token);
            }
            else
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "api/console/chat")
                {
                    Content = JsonContent.Create(BuildPayload(fromAgent, sessionId, text, null))
                };
                req.Headers.Add(AgentHeader, effectiveAgentProfile);
                try
                {
                    first = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
                catch (HttpRequestException ex) when (!ct.IsCancellationRequested && IsConnectFailure(ex))
                {
                    await Task.Delay(TransientRetryDelay, ct);
                    using var again = new HttpRequestMessage(HttpMethod.Post, "api/console/chat")
                    {
                        Content = JsonContent.Create(BuildPayload(fromAgent, sessionId, text, null))
                    };
                    again.Headers.Add(AgentHeader, effectiveAgentProfile);
                    first = await _http.SendAsync(again, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
            }
            using var resp = first;
            if (!resp.IsSuccessStatusCode)
            {
                return new NativeChatResult(false, sessionId, null, await ReadDetail(resp, cts.Token), (int)resp.StatusCode);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? finalText = null;
            string? error = null;
            var reasoningMsgIds = new HashSet<string>(StringComparer.Ordinal);
            var textByMsgId = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            var msgOrder = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                var parsed = ParseSseFinal(line);
                if (parsed.Text is not null) finalText = parsed.Text;
                if (parsed.Error is not null) error = parsed.Error;

                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line[6..]);
                        var root = doc.RootElement;
                        var obj = Str(root, "object");
                        if (obj == "message")
                        {
                            var mid = Str(root, "id") ?? "default";
                            var mtype = Str(root, "type") ?? "message";
                            var mrole = Str(root, "role") ?? "assistant";
                            if (mtype is "reasoning" or "thinking")
                            {
                                reasoningMsgIds.Add(mid);
                            }
                            else if (mrole == "assistant" && mtype == "message" && root.TryGetProperty("content", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var part in cArr.EnumerateArray())
                                {
                                    if (Str(part, "type") == "text")
                                    {
                                        var txt = Str(part, "text");
                                        if (!string.IsNullOrEmpty(txt))
                                        {
                                            if (!textByMsgId.TryGetValue(mid, out var sb))
                                            {
                                                sb = new StringBuilder();
                                                textByMsgId[mid] = sb;
                                                msgOrder.Add(mid);
                                            }
                                            var isDelta = part.TryGetProperty("delta", out var dProp) && dProp.ValueKind == JsonValueKind.True;
                                            if (isDelta) sb.Append(txt);
                                            else { sb.Clear(); sb.Append(txt); }
                                        }
                                    }
                                }
                            }
                        }
                        else if (obj == "content" && Str(root, "type") == "text")
                        {
                            var mid = Str(root, "msg_id") ?? "default";
                            if (!reasoningMsgIds.Contains(mid))
                            {
                                var txt = Str(root, "text");
                                if (!string.IsNullOrEmpty(txt))
                                {
                                    if (!textByMsgId.TryGetValue(mid, out var sb))
                                    {
                                        sb = new StringBuilder();
                                        textByMsgId[mid] = sb;
                                        msgOrder.Add(mid);
                                    }
                                    var isDelta = root.TryGetProperty("delta", out var dProp) && dProp.ValueKind == JsonValueKind.True;
                                    if (isDelta) sb.Append(txt);
                                    else { sb.Clear(); sb.Append(txt); }
                                }
                            }
                        }
                    }
                    catch (JsonException) {}
                }
            }

            if (string.IsNullOrWhiteSpace(finalText) && msgOrder.Count > 0)
            {
                var combined = string.Join("\n\n", msgOrder.Select(id => textByMsgId[id].ToString().Trim()).Where(s => !string.IsNullOrEmpty(s)));
                if (!string.IsNullOrWhiteSpace(combined))
                {
                    finalText = combined;
                }
            }

            return !string.IsNullOrWhiteSpace(finalText)
                ? new NativeChatResult(true, sessionId, finalText, null, 200)
                : new NativeChatResult(false, sessionId, null, error ?? "No completed response from target agent", 200);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await StopChatAsync(sessionId, toAgent, CancellationToken.None);
            return new NativeChatResult(false, sessionId, null, "timed_out", 504);
        }
    }

    public async Task<NativeSubmitResult> SubmitAsync(string fromAgent, string toAgent, string text, int timeoutSeconds, CancellationToken ct)
    {
        var sessionId = BuildSessionId(fromAgent, toAgent);
        using var cts = Linked(ct, 60);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/console/chat/task")
        {
            Content = JsonContent.Create(BuildPayload(fromAgent, sessionId, text, timeoutSeconds))
        };
        req.Headers.Add(AgentHeader, toAgent);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, cts.Token);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested && IsConnectFailure(ex))
        {
            // The request never reached QwenPaw, so resubmitting cannot create a duplicate task.
            await Task.Delay(TransientRetryDelay, ct);
            using var again = new HttpRequestMessage(HttpMethod.Post, "api/console/chat/task")
            {
                Content = JsonContent.Create(BuildPayload(fromAgent, sessionId, text, timeoutSeconds))
            };
            again.Headers.Add(AgentHeader, toAgent);
            resp = await _http.SendAsync(again, cts.Token);
        }
        using var _resp = resp;
        if (!resp.IsSuccessStatusCode)
        {
            return new NativeSubmitResult(false, sessionId, null, null, await ReadDetail(resp, cts.Token), (int)resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var taskId = Str(doc.RootElement, "task_id");
        int? timeout = doc.RootElement.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var tv) ? tv : null;
        return string.IsNullOrWhiteSpace(taskId)
            ? new NativeSubmitResult(false, sessionId, null, timeout, "No task_id returned from AgentCore", (int)resp.StatusCode)
            : new NativeSubmitResult(true, sessionId, taskId, timeout, null, (int)resp.StatusCode);
    }

    public async Task<NativeTaskStatus> GetTaskAsync(string nativeTaskId, string? toAgent, CancellationToken ct)
    {
        try
        {
            return await GetTaskOnceAsync(nativeTaskId, toAgent, ct);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            // Read-only status poll: one retry when the runtime drops the connection under load.
            await Task.Delay(TransientRetryDelay, ct);
            return await GetTaskOnceAsync(nativeTaskId, toAgent, ct);
        }
    }

    private async Task<NativeTaskStatus> GetTaskOnceAsync(string nativeTaskId, string? toAgent, CancellationToken ct)
    {
        using var cts = Linked(ct, 30);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/console/chat/task/{Uri.EscapeDataString(nativeTaskId)}");
        if (!string.IsNullOrWhiteSpace(toAgent)) req.Headers.Add(AgentHeader, toAgent);
        using var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            return new NativeTaskStatus("unknown", null, null, await ReadDetail(resp, cts.Token), null, (int)resp.StatusCode);
        }

        return ParseTaskStatus(await resp.Content.ReadAsStringAsync(cts.Token));
    }

    public async Task<bool> StopChatAsync(string sessionId, string toAgent, CancellationToken ct)
    {
        try
        {
            using var cts = Linked(ct, 15);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/console/chat/stop?chat_id={Uri.EscapeDataString(sessionId)}");
            req.Headers.Add(AgentHeader, toAgent);
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.TryGetProperty("stopped", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> UpsertSkillAsync(string agentId, string skillName, string content, CancellationToken ct)
    {
        using var cts = Linked(ct, 60);
        // Every skill write (create / save / enable) makes QwenPaw hot-reload the whole agent, which restarts all
        // of its stdio MCP servers. Read first and only write what actually differs.
        var current = await TryGetSkillAsync(agentId, skillName, cts.Token);
        if (current is { } cur && cur.Content == content)
        {
            if (cur.Enabled) return true;
            using var enableOnly = new HttpRequestMessage(HttpMethod.Post, $"api/skills/{Uri.EscapeDataString(skillName)}/enable");
            enableOnly.Headers.Add(AgentHeader, agentId);
            using var enableOnlyResp = await _http.SendAsync(enableOnly, cts.Token);
            return enableOnlyResp.IsSuccessStatusCode;
        }
        if (current is not null)
        {
            using var update = new HttpRequestMessage(HttpMethod.Put, "api/skills/save")
            {
                Content = JsonContent.Create(new { name = skillName, content, overwrite = true })
            };
            update.Headers.Add(AgentHeader, agentId);
            using var updateResp = await _http.SendAsync(update, cts.Token);
            if (!updateResp.IsSuccessStatusCode) return false;
            if (current.Value.Enabled) return true;
            using var enableAfter = new HttpRequestMessage(HttpMethod.Post, $"api/skills/{Uri.EscapeDataString(skillName)}/enable");
            enableAfter.Headers.Add(AgentHeader, agentId);
            using var __ = await _http.SendAsync(enableAfter, cts.Token);
            return true;
        }
        using var create = new HttpRequestMessage(HttpMethod.Post, "api/skills")
        {
            Content = JsonContent.Create(new { name = skillName, content, enable = true })
        };
        create.Headers.Add(AgentHeader, agentId);
        using var createResp = await _http.SendAsync(create, cts.Token);
        if (createResp.IsSuccessStatusCode) return true;
        if (createResp.StatusCode != HttpStatusCode.Conflict) return false;

        using var save = new HttpRequestMessage(HttpMethod.Put, "api/skills/save")
        {
            Content = JsonContent.Create(new { name = skillName, content, overwrite = true })
        };
        save.Headers.Add(AgentHeader, agentId);
        using var saveResp = await _http.SendAsync(save, cts.Token);
        if (!saveResp.IsSuccessStatusCode) return false;

        using var enable = new HttpRequestMessage(HttpMethod.Post, $"api/skills/{Uri.EscapeDataString(skillName)}/enable");
        enable.Headers.Add(AgentHeader, agentId);
        using var _ = await _http.SendAsync(enable, cts.Token);
        return true;
    }

    private async Task<(string Content, bool Enabled)?> TryGetSkillAsync(string agentId, string skillName, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"api/skills/{Uri.EscapeDataString(skillName)}");
            req.Headers.Add(AgentHeader, agentId);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            var enabled = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            return (content, enabled);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DetectedNativeDelegation>> ScanAgentDelegationsAsync(string agentId, int maxChats, CancellationToken ct)
    {
        using var cts = Linked(ct, 60);
        using var listReq = new HttpRequestMessage(HttpMethod.Get, "api/chats");
        listReq.Headers.Add(AgentHeader, agentId);
        using var listResp = await _http.SendAsync(listReq, cts.Token);
        if (!listResp.IsSuccessStatusCode) return [];

        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(cts.Token));
        var chats = listDoc.RootElement.ValueKind == JsonValueKind.Array
            ? listDoc.RootElement.EnumerateArray()
                .Select(c => (Id: Str(c, "id"), Updated: Str(c, "updated_at")))
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .OrderByDescending(c => c.Updated, StringComparer.Ordinal)
                .Take(maxChats)
                .ToList()
            : [];

        var detected = new List<DetectedNativeDelegation>();
        foreach (var chat in chats)
        {
            using var histReq = new HttpRequestMessage(HttpMethod.Get, $"api/chats/{Uri.EscapeDataString(chat.Id!)}");
            histReq.Headers.Add(AgentHeader, agentId);
            using var histResp = await _http.SendAsync(histReq, cts.Token);
            if (!histResp.IsSuccessStatusCode) continue;
            detected.AddRange(ExtractDelegations(agentId, chat.Id!, await histResp.Content.ReadAsStringAsync(cts.Token)));
        }

        return detected;
    }

    public static (string? Text, string? Error) ParseSseFinal(string line)
    {
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(line[6..]);
            var root = doc.RootElement;
            if (Str(root, "type") == "turn_usage" || Str(root, "object") != "response") return (null, null);
            var status = Str(root, "status");
            if (status == "failed")
            {
                return (null, root.TryGetProperty("error", out var err) ? (Str(err, "message") ?? err.ToString()) : "failed");
            }

            return status == "completed" ? (ExtractOutputText(root), null) : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public static NativeTaskStatus ParseTaskStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = Str(root, "status") ?? "unknown";
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return new NativeTaskStatus(status, null, null, null, null, 200);
        }

        var resultStatus = Str(result, "status");
        string? error = null;
        if (result.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            error = Str(err, "message") ?? err.ToString();
        }

        return new NativeTaskStatus(status, resultStatus, ExtractOutputText(result), error, Str(result, "session_id"), 200);
    }

    public static IReadOnlyList<DetectedNativeDelegation> ExtractDelegations(string agentId, string chatId, string historyJson)
    {
        using var doc = JsonDocument.Parse(historyJson);
        var messages = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("messages", out var m) ? m : default;
        if (messages.ValueKind != JsonValueKind.Array) return [];

        var calls = new Dictionary<string, (string Tool, string To, string Text, DateTimeOffset? At, string? Session, int Order)>();
        var outputs = new Dictionary<string, string>();
        foreach (var msg in messages.EnumerateArray())
        {
            var type = Str(msg, "type");
            if (type is not ("plugin_call" or "plugin_call_output")) continue;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            DateTimeOffset? at = DateTimeOffset.TryParse(Str(msg, "created_at"), out var ts) ? ts : null;
            foreach (var c in content.EnumerateArray())
            {
                if (!c.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                var name = Str(data, "name");
                var callId = Str(data, "call_id");
                if (callId is null || name is not ("chat_with_agent" or "submit_to_agent")) continue;
                if (type == "plugin_call")
                {
                    try
                    {
                        using var args = JsonDocument.Parse(Str(data, "arguments") ?? "{}");
                        var to = Str(args.RootElement, "to_agent");
                        if (!string.IsNullOrWhiteSpace(to))
                        {
                            calls[callId] = (name, to.Trim(), Str(args.RootElement, "text") ?? "", at, Str(args.RootElement, "session_id"), calls.Count);
                        }
                    }
                    catch (JsonException)
                    {
                        // malformed arguments are not a delegation record
                    }
                }
                else
                {
                    outputs[callId] = ExtractToolOutputText(data);
                }
            }
        }

        return calls.OrderBy(kv => kv.Value.Order).Select(kv =>
        {
            outputs.TryGetValue(kv.Key, out var output);
            return new DetectedNativeDelegation(
                kv.Key,
                kv.Value.Tool,
                agentId,
                kv.Value.To,
                kv.Value.Text,
                Tag(output, "SESSION") ?? (string.IsNullOrWhiteSpace(kv.Value.Session) ? null : kv.Value.Session!.Trim()),
                Tag(output, "TASK_ID"),
                output,
                chatId,
                kv.Value.At);
        }).ToList();
    }

    private static string ExtractToolOutputText(JsonElement data)
    {
        if (!data.TryGetProperty("output", out var output)) return "";
        var raw = output.ValueKind == JsonValueKind.String ? output.GetString() ?? "" : output.ToString();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return string.Join("\n", doc.RootElement.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Object && Str(x, "type") == "text")
                    .Select(x => Str(x, "text")));
            }
        }
        catch (JsonException)
        {
            // plain text output
        }

        return raw;
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array || output.GetArrayLength() == 0)
        {
            return null;
        }

        var last = output[output.GetArrayLength() - 1];
        if (!last.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var parts = content.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object && Str(c, "type") == "text")
            .Select(c => Str(c, "text") ?? "")
            .ToList();
        return parts.Count == 0 ? null : string.Join("\n", parts).Trim();
    }

    private static string? Tag(string? text, string tag)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = Regex.Match(text, $@"\[{tag}:\s*([^\]]+)\]");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static async Task<string> ReadDetail(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Str(doc.RootElement, "detail") ?? body;
        }
        catch (JsonException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }

    internal static TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    private static bool IsConnectFailure(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError;

    private static CancellationTokenSource Linked(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, seconds)));
        return cts;
    }

    [GeneratedRegex(@"^\[(Agent|来自智能体)\s+\w+")]
    private static partial Regex AgentPrefixRegex();
}

public sealed class QwenPawUnavailableException(string message, Exception inner) : Exception(message, inner);
