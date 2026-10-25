using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.SubAgents;

public sealed record SkillSyncStatus(
    [property: System.Text.Json.Serialization.JsonPropertyName("parent_agent_id")] string ParentAgentId,
    [property: System.Text.Json.Serialization.JsonPropertyName("state")] string State,
    [property: System.Text.Json.Serialization.JsonPropertyName("requested_at")] DateTimeOffset? RequestedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("synced_at")] DateTimeOffset? SyncedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("duration_ms")] long? DurationMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error);

public interface ISubAgentSkillSyncQueue
{
    SkillSyncStatus Enqueue(string parentAgentId);

    SkillSyncStatus GetStatus(string parentAgentId);
}

/// <summary>
/// Coalescing background queue: binding changes return immediately and the generated
/// agent360-subagents skill is pushed to the QwenPaw runtime off the request path.
/// Multiple changes for the same parent collapse into one sync of the latest bindings.
/// </summary>
public sealed class SubAgentSkillSyncQueue(
    IServiceScopeFactory scopes,
    IMemoryCache cache,
    ILogger<SubAgentSkillSyncQueue> logger) : BackgroundService, ISubAgentSkillSyncQueue
{
    private const int MaxAttempts = 3;
    internal TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(2);
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, SkillSyncStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    public SkillSyncStatus Enqueue(string parentAgentId)
    {
        var parent = SubAgentService.NormalizeAgentId(parentAgentId);
        var prev = GetStatus(parent);
        var status = prev with { State = "pending", RequestedAt = DateTimeOffset.UtcNow, Error = null };
        _status[parent] = status;
        if (_pending.TryAdd(parent, 0)) _channel.Writer.TryWrite(parent);
        return status;
    }

    public SkillSyncStatus GetStatus(string parentAgentId)
    {
        var parent = SubAgentService.NormalizeAgentId(parentAgentId);
        return _status.TryGetValue(parent, out var s) ? s : new SkillSyncStatus(parent, "idle", null, null, null, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var parent in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            _pending.TryRemove(parent, out _);
            _status[parent] = GetStatus(parent) with { State = "syncing" };
            var started = System.Diagnostics.Stopwatch.StartNew();
            string? error = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<SubAgentService>();
                    var (ok, _) = await svc.SyncSkillAsync(parent, force: attempt > 1, stoppingToken);
                    if (ok) { error = null; break; }
                    error = "AgentCore runtime rejected the skill upsert";
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    error = ex.Message;
                }
                if (attempt < MaxAttempts) await Task.Delay(RetryBackoff * attempt, stoppingToken);
            }
            started.Stop();
            InvalidateSkillCaches(parent);
            // A newer request may have arrived while syncing; keep it pending in that case.
            var state = _pending.ContainsKey(parent) ? "pending" : error is null ? "synced" : "failed";
            _status[parent] = GetStatus(parent) with
            {
                State = state,
                SyncedAt = error is null ? DateTimeOffset.UtcNow : GetStatus(parent).SyncedAt,
                DurationMs = started.ElapsedMilliseconds,
                Error = error
            };
            if (error is null)
                logger.LogInformation("Synced {Skill} into {Agent} in {Ms} ms", SubAgentSkillGenerator.SkillName, parent, started.ElapsedMilliseconds);
            else
                logger.LogWarning("Skill sync for {Agent} failed after {Attempts} attempts: {Error}", parent, MaxAttempts, error);
        }
    }

    private void InvalidateSkillCaches(string parent)
    {
        var key = parent.ToLowerInvariant();
        cache.Remove($"agent360:proxy:{key}:skills");
        cache.Remove($"agent360:skills:{key}");
    }
}
