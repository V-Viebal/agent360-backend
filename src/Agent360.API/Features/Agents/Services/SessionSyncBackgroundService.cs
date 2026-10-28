using System.Collections.Concurrent;
using System.Text.Json;
using Agent360.API.Features.Agents.Analytics;

namespace Agent360.API.Features.Agents.Services;

/// <summary>
/// High-performance background worker that polls active and newly created chat sessions across
/// QwenPaw runtimes (Telegram, Zalo, Console, Cron, Matrix, SubAgent) and broadcasts real-time
/// session list and turn deltas through SessionMultiplexerHub.
/// </summary>
public sealed class SessionSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    SessionQueueManager queueManager,
    SessionMultiplexerHub multiplexerHub,
    ILogger<SessionSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3.0);
    private readonly ConcurrentDictionary<string, string> _activeTrackedSessions = new(StringComparer.OrdinalIgnoreCase);

    // Per-agent known session fingerprints: agentKey -> (chatId -> (Name, UpdatedAt, Status))
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (string Name, string UpdatedAt, string Status)>> _agentKnownSessions = new(StringComparer.OrdinalIgnoreCase);
    private int _cycleCounter;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await SyncActiveSessionsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "SessionSync cycle encountered transient error");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncActiveSessionsAsync(CancellationToken ct)
    {
        _cycleCounter++;
        using var scope = scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IQwenPawClient>();
        var routing = scope.ServiceProvider.GetRequiredService<IFleetInstanceRoutingService>();

        // 1. Hot agents: primary agents, agents with active turns, and agents recently viewed by connected clients
        var agentsToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mons", "default" };
        foreach (var ag in queueManager.GetActiveAgentSessions().Keys)
        {
            if (!string.IsNullOrWhiteSpace(ag)) agentsToCheck.Add(ag);
        }
        foreach (var ag in _activeTrackedSessions.Values)
        {
            if (!string.IsNullOrWhiteSpace(ag)) agentsToCheck.Add(ag);
        }
        foreach (var ag in multiplexerHub.GetRecentlySubscribedAgents(TimeSpan.FromMinutes(3)))
        {
            if (!string.IsNullOrWhiteSpace(ag)) agentsToCheck.Add(ag);
        }

        // Every 4th cycle (~12s), if there are connected WebSocket/SSE clients, also sweep all enabled fleet agents
        if (_cycleCounter % 4 == 0 && (multiplexerHub.ConnectedWebSocketsCount > 0 || multiplexerHub.ConnectedSseCount > 0))
        {
            try
            {
                var fleetAgents = await client.GetAgentsAsync(ct);
                foreach (var fa in fleetAgents.Where(a => a.Enabled))
                {
                    if (!string.IsNullOrWhiteSpace(fa.Slug)) agentsToCheck.Add(fa.Slug);
                }
            }
            catch { }
        }

        var discoveredActiveThisCycle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successfullyCheckedAgents = new ConcurrentBag<string>();

        // Group by underlying runtime instance so we never flood a single node with concurrent requests
        var groupedByInstance = agentsToCheck
            .Select(ag => (Agent: ag, Target: routing.ResolveTarget(ag)))
            .Where(x => x.Target.IsQwenPaw)
            .GroupBy(x => x.Target.Instance?.Key ?? x.Agent, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Parallel.ForEachAsync(
            groupedByInstance,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(6, groupedByInstance.Count)), CancellationToken = ct },
            async (instanceGroup, token) =>
            {
                foreach (var item in instanceGroup)
                {
                    if (token.IsCancellationRequested) break;
                    await SyncSingleAgentChatsAsync(client, item.Agent, discoveredActiveThisCycle, successfullyCheckedAgents, token);
                }
            });

        var checkedSet = new HashSet<string>(successfullyCheckedAgents, StringComparer.OrdinalIgnoreCase);

        // Reconcile sessions that finished their turn in this cycle
        foreach (var (trackedId, trackedAgent) in _activeTrackedSessions.ToArray())
        {
            if (checkedSet.Contains(trackedAgent) && !discoveredActiveThisCycle.Contains(trackedId))
            {
                _activeTrackedSessions.TryRemove(trackedId, out _);
                queueManager.SetSessionTurnActive(trackedId, false, trackedAgent);
                if (!trackedId.StartsWith("telegram:", StringComparison.OrdinalIgnoreCase) &&
                    !trackedId.StartsWith("zalo:", StringComparison.OrdinalIgnoreCase))
                {
                    ChannelResponseStore.SetUnread(trackedId, true);
                }
            }
        }
    }

    private async Task SyncSingleAgentChatsAsync(
        IQwenPawClient client,
        string agent,
        HashSet<string> discoveredActiveThisCycle,
        ConcurrentBag<string> successfullyCheckedAgents,
        CancellationToken ct)
    {
        try
        {
            var normalizedAgent = FleetInstanceRoutingService.NormalizeAgentId(agent).ToLowerInvariant();
            // Bypass 10s stale cache for hot sync so new sessions from Telegram/Zalo/other devices appear within ~3s
            var chatsRaw = await client.RefreshAgentChatsRawAsync(agent, ct);
            if (string.IsNullOrWhiteSpace(chatsRaw)) return;

            using var doc = JsonDocument.Parse(chatsRaw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            successfullyCheckedAgents.Add(normalizedAgent);

            bool isFirstBaselineForAgent = !_agentKnownSessions.ContainsKey(normalizedAgent);
            var knownMap = _agentKnownSessions.GetOrAdd(
                normalizedAgent,
                _ => new ConcurrentDictionary<string, (string Name, string UpdatedAt, string Status)>(StringComparer.OrdinalIgnoreCase));

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                var rawName = el.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var channel = el.TryGetProperty("channel", out var chProp) ? chProp.GetString() : "console";
                var updatedAt = el.TryGetProperty("updated_at", out var upProp) ? upProp.GetString() : "";
                var createdAt = el.TryGetProperty("created_at", out var crProp) ? crProp.GetString() : "";
                var status = el.TryGetProperty("status", out var statProp) ? statProp.GetString() ?? "idle" : "idle";

                var effectiveId = !string.IsNullOrWhiteSpace(id) ? id : sid;
                if (string.IsNullOrWhiteSpace(effectiveId)) continue;

                var cleanName = rawName ?? "";
                var ctxIdx = cleanName.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                if (ctxIdx >= 0) cleanName = cleanName[..ctxIdx].TrimEnd();

                var isRunning = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase);

                if (isRunning)
                {
                    lock (discoveredActiveThisCycle)
                    {
                        if (!string.IsNullOrWhiteSpace(id)) discoveredActiveThisCycle.Add(id);
                        if (!string.IsNullOrWhiteSpace(sid)) discoveredActiveThisCycle.Add(sid);
                    }

                    if (!string.IsNullOrWhiteSpace(id) && !_activeTrackedSessions.ContainsKey(id))
                    {
                        _activeTrackedSessions[id] = normalizedAgent;
                        queueManager.SetSessionTurnActive(id, true, normalizedAgent);
                    }
                    if (!string.IsNullOrWhiteSpace(sid) && !_activeTrackedSessions.ContainsKey(sid))
                    {
                        _activeTrackedSessions[sid] = normalizedAgent;
                        queueManager.SetSessionTurnActive(sid, true, normalizedAgent);
                    }
                }

                var currentTuple = (Name: cleanName, UpdatedAt: updatedAt ?? "", Status: status);
                if (!knownMap.TryGetValue(effectiveId, out var prevTuple))
                {
                    knownMap[effectiveId] = currentTuple;

                    // If we already had a baseline for this agent, this is a newly created chat session
                    // (e.g., from Telegram, Zalo, Webhook, Cron, or another browser/device)!
                    if (!isFirstBaselineForAgent)
                    {
                        var upsertPayload = JsonSerializer.Serialize(new
                        {
                            type = "chat_session_upsert",
                            agent_id = normalizedAgent,
                            is_new = true,
                            session = new
                            {
                                id = effectiveId,
                                session_id = sid ?? effectiveId,
                                title = string.IsNullOrWhiteSpace(cleanName) ? "Cuộc hội thoại" : cleanName,
                                name = cleanName,
                                channel = channel ?? "console",
                                status = isRunning ? "running" : status,
                                is_running = isRunning,
                                updated_at = !string.IsNullOrWhiteSpace(updatedAt) ? updatedAt : DateTimeOffset.UtcNow.ToString("O"),
                                created_at = createdAt,
                                unread = true
                            },
                            timestamp = DateTimeOffset.UtcNow.ToString("O")
                        });
                        _ = multiplexerHub.BroadcastAsync(upsertPayload);
                    }
                }
                else if (!string.Equals(prevTuple.Name, currentTuple.Name, StringComparison.Ordinal) ||
                         !string.Equals(prevTuple.UpdatedAt, currentTuple.UpdatedAt, StringComparison.Ordinal))
                {
                    knownMap[effectiveId] = currentTuple;

                    // Broadcast title or activity timestamp change so all connected devices reorder & update title immediately
                    var upsertPayload = JsonSerializer.Serialize(new
                    {
                        type = "chat_session_upsert",
                        agent_id = normalizedAgent,
                        is_new = false,
                        session = new
                        {
                            id = effectiveId,
                            session_id = sid ?? effectiveId,
                            title = string.IsNullOrWhiteSpace(cleanName) ? "Cuộc hội thoại" : cleanName,
                            name = cleanName,
                            channel = channel ?? "console",
                            status = isRunning ? "running" : status,
                            is_running = isRunning,
                            updated_at = !string.IsNullOrWhiteSpace(updatedAt) ? updatedAt : DateTimeOffset.UtcNow.ToString("O"),
                            created_at = createdAt,
                            unread = !string.Equals(prevTuple.UpdatedAt, currentTuple.UpdatedAt, StringComparison.Ordinal)
                        },
                        timestamp = DateTimeOffset.UtcNow.ToString("O")
                    });
                    _ = multiplexerHub.BroadcastAsync(upsertPayload);
                }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Ignore transient network hiccups per agent
        }
    }
}
