using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Features.Agents.SubAgents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Webhooks;

public sealed record OutboundWebhookTargetDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("events")] string[]? Events = null,
    [property: JsonPropertyName("secret")] string? Secret = null,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers = null);

public sealed record AgentWebhookConfigDto(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("require_hmac")] bool RequireHmac,
    [property: JsonPropertyName("signing_secret_masked")] string SigningSecretMasked,
    [property: JsonPropertyName("signing_secret_preview")] string SigningSecretPreview,
    [property: JsonPropertyName("inbound_webhook_url")] string InboundWebhookUrl,
    [property: JsonPropertyName("agentteams_webhook_url")] string AgentTeamsWebhookUrl,
    [property: JsonPropertyName("allowed_events")] string[] AllowedEvents,
    [property: JsonPropertyName("default_channel")] string DefaultChannel,
    [property: JsonPropertyName("default_session_prefix")] string DefaultSessionPrefix,
    [property: JsonPropertyName("auto_trigger_agent_turn")] bool AutoTriggerAgentTurn,
    [property: JsonPropertyName("forward_to_matrix_room")] bool ForwardToMatrixRoom,
    [property: JsonPropertyName("rate_limit_per_minute")] int RateLimitPerMinute,
    [property: JsonPropertyName("deduplication_window_seconds")] int DeduplicationWindowSeconds,
    [property: JsonPropertyName("prompt_template")] string PromptTemplate,
    [property: JsonPropertyName("outbound_webhooks")] List<OutboundWebhookTargetDto> OutboundWebhooks,
    [property: JsonPropertyName("matrix_channel_enabled")] bool MatrixChannelEnabled,
    [property: JsonPropertyName("matrix_homeserver")] string? MatrixHomeserver,
    [property: JsonPropertyName("matrix_user_id")] string? MatrixUserId,
    [property: JsonPropertyName("qwenpaw_runtime_status")] string QwenPawRuntimeStatus,
    [property: JsonPropertyName("qwenpaw_node")] string QwenPawNode,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

public sealed record UpdateAgentWebhookConfigRequest(
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("require_hmac")] bool? RequireHmac = null,
    [property: JsonPropertyName("allowed_events")] string[]? AllowedEvents = null,
    [property: JsonPropertyName("default_channel")] string? DefaultChannel = null,
    [property: JsonPropertyName("default_session_prefix")] string? DefaultSessionPrefix = null,
    [property: JsonPropertyName("auto_trigger_agent_turn")] bool? AutoTriggerAgentTurn = null,
    [property: JsonPropertyName("forward_to_matrix_room")] bool? ForwardToMatrixRoom = null,
    [property: JsonPropertyName("rate_limit_per_minute")] int? RateLimitPerMinute = null,
    [property: JsonPropertyName("deduplication_window_seconds")] int? DeduplicationWindowSeconds = null,
    [property: JsonPropertyName("prompt_template")] string? PromptTemplate = null,
    [property: JsonPropertyName("outbound_webhooks")] List<OutboundWebhookTargetDto>? OutboundWebhooks = null,
    [property: JsonPropertyName("matrix_channel_enabled")] bool? MatrixChannelEnabled = null,
    [property: JsonPropertyName("matrix_homeserver")] string? MatrixHomeserver = null,
    [property: JsonPropertyName("matrix_user_id")] string? MatrixUserId = null);

public sealed record AgentWebhookDeliveryRecordDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("direction")] string Direction, // "inbound" | "outbound" | "qwenpaw_bridge"
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("status")] string Status, // "delivered" | "triggered" | "rejected" | "duplicate" | "failed"
    [property: JsonPropertyName("http_status")] int HttpStatus,
    [property: JsonPropertyName("latency_ms")] long LatencyMs,
    [property: JsonPropertyName("payload_preview")] string PayloadPreview,
    [property: JsonPropertyName("response_preview")] string? ResponsePreview,
    [property: JsonPropertyName("signature_verified")] bool SignatureVerified,
    [property: JsonPropertyName("timestamp")] string Timestamp);

public sealed record AgentInboundWebhookPayload(
    [property: JsonPropertyName("event_id")] string? EventId = null,
    [property: JsonPropertyName("event_type")] string? EventType = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("channel")] string? Channel = null,
    [property: JsonPropertyName("user_id")] string? UserId = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("trigger_turn")] bool? TriggerTurn = null,
    [property: JsonPropertyName("wait_for_completion")] bool? WaitForCompletion = null,
    [property: JsonPropertyName("loop_mode")] string? LoopMode = null,
    [property: JsonPropertyName("room_id")] string? RoomId = null,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

public sealed record QwenPawBridgeProbeResultDto(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("node")] string Node,
    [property: JsonPropertyName("instance_name")] string InstanceName,
    [property: JsonPropertyName("is_qwenpaw_runtime")] bool IsQwenPawRuntime,
    [property: JsonPropertyName("runtime_reachable")] bool RuntimeReachable,
    [property: JsonPropertyName("runtime_status")] string RuntimeStatus,
    [property: JsonPropertyName("latency_ms")] long LatencyMs,
    [property: JsonPropertyName("active_channels")] string[] ActiveChannels,
    [property: JsonPropertyName("matrix_configured")] bool MatrixConfigured,
    [property: JsonPropertyName("heartbeat_enabled")] bool HeartbeatEnabled,
    [property: JsonPropertyName("running_task_count")] int RunningTaskCount,
    [property: JsonPropertyName("checked_at")] string CheckedAt,
    [property: JsonPropertyName("message")] string Message);

internal sealed class StoredAgentWebhookState
{
    public string AgentId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool RequireHmac { get; set; } = false;
    public string SigningSecret { get; set; } = "";
    public string[] AllowedEvents { get; set; } = ["task.trigger", "message.inbound", "alert.incident", "matrix.room.message", "cron.wake", "custom.webhook"];
    public string DefaultChannel { get; set; } = "console";
    public string DefaultSessionPrefix { get; set; } = "webhook";
    public bool AutoTriggerAgentTurn { get; set; } = true;
    public bool ForwardToMatrixRoom { get; set; } = false;
    public int RateLimitPerMinute { get; set; } = 60;
    public int DeduplicationWindowSeconds { get; set; } = 300;
    public string PromptTemplate { get; set; } = "[WEBHOOK EVENT: {{event_type}} | Source: {{source}}]\n{{message}}";
    public List<OutboundWebhookTargetDto> OutboundWebhooks { get; set; } = [];
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed class AgentWebhookStore
{
    private readonly string _configsPath;
    private readonly string _deliveriesPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, StoredAgentWebhookState> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<AgentWebhookDeliveryRecordDto> _deliveries = new();

    public AgentWebhookStore(IOptions<SubAgentOptions> options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.Value.StoragePath)
            ? options.Value.StoragePath
            : Path.Combine(env.ContentRootPath, options.Value.StoragePath);
        Directory.CreateDirectory(dir);
        _configsPath = Path.Combine(dir, "agent_webhook_configs.json");
        _deliveriesPath = Path.Combine(dir, "agent_webhook_deliveries.json");

        if (File.Exists(_configsPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, StoredAgentWebhookState>>(File.ReadAllText(_configsPath));
                if (loaded is not null)
                {
                    foreach (var (k, v) in loaded) _configs[k] = v;
                }
            }
            catch { }
        }

        if (File.Exists(_deliveriesPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<AgentWebhookDeliveryRecordDto>>(File.ReadAllText(_deliveriesPath));
                if (loaded is not null)
                {
                    foreach (var d in loaded.TakeLast(300)) _deliveries.Enqueue(d);
                }
            }
            catch { }
        }
    }

    private static string NormalizeKey(string agentId) =>
        FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();

    private static string GenerateSecret(string agentKey)
    {
        var randomBytes = RandomNumberGenerator.GetBytes(24);
        return $"whsec_{agentKey.Replace('-', '_')}_{Convert.ToHexString(randomBytes).ToLowerInvariant()}";
    }

    internal StoredAgentWebhookState GetOrCreateState(string agentId)
    {
        var key = NormalizeKey(agentId);
        return _configs.GetOrAdd(key, k => new StoredAgentWebhookState
        {
            AgentId = k,
            Enabled = true,
            RequireHmac = false,
            SigningSecret = GenerateSecret(k),
            AllowedEvents = ["task.trigger", "message.inbound", "alert.incident", "matrix.room.message", "cron.wake", "custom.webhook"],
            DefaultChannel = "console",
            DefaultSessionPrefix = $"wh-{k}",
            AutoTriggerAgentTurn = true,
            ForwardToMatrixRoom = false,
            RateLimitPerMinute = 60,
            DeduplicationWindowSeconds = 300,
            PromptTemplate = "[WEBHOOK EVENT: {{event_type}} | Source: {{source}}]\n{{message}}",
            OutboundWebhooks = [],
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    internal async Task<StoredAgentWebhookState> SaveStateAsync(string agentId, Action<StoredAgentWebhookState> mutate)
    {
        var state = GetOrCreateState(agentId);
        await _lock.WaitAsync();
        try
        {
            mutate(state);
            state.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            _configs[NormalizeKey(agentId)] = state;
            var tmp = _configsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_configs.ToDictionary(k => k.Key, k => k.Value), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _configsPath, true);
        }
        finally
        {
            _lock.Release();
        }
        return state;
    }

    internal async Task<(string NewSecret, StoredAgentWebhookState State)> RotateSecretAsync(string agentId)
    {
        var key = NormalizeKey(agentId);
        var newSecret = GenerateSecret(key);
        var state = await SaveStateAsync(key, s => s.SigningSecret = newSecret);
        return (newSecret, state);
    }

    public void RecordDelivery(AgentWebhookDeliveryRecordDto record)
    {
        _deliveries.Enqueue(record);
        while (_deliveries.Count > 400)
        {
            _deliveries.TryDequeue(out _);
        }

        _ = Task.Run(async () =>
        {
            if (!await _lock.WaitAsync(TimeSpan.FromSeconds(2))) return;
            try
            {
                var snapshot = _deliveries.ToArray();
                var tmp = _deliveriesPath + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, _deliveriesPath, true);
            }
            catch { }
            finally
            {
                _lock.Release();
            }
        });
    }

    public IReadOnlyList<AgentWebhookDeliveryRecordDto> GetDeliveries(string? agentId = null, int limit = 50)
    {
        var q = _deliveries.Reverse().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var key = NormalizeKey(agentId);
            q = q.Where(d => string.Equals(d.AgentId, key, StringComparison.OrdinalIgnoreCase));
        }
        return q.Take(Math.Clamp(limit, 1, 200)).ToList();
    }
}
