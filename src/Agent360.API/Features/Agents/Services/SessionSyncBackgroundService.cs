using System.Collections.Concurrent;
using System.Text.Json;
using Agent360.API.Features.Agents.Analytics;

namespace Agent360.API.Features.Agents.Services;

/// <summary>
/// Background worker that periodically polls active chat turn states from QwenPaw runtimes
/// (Telegram, console, cron, subagent executions) and keeps SessionQueueManager and SessionMultiplexerHub
/// synchronized in real time.
/// </summary>
public sealed class SessionSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    SessionQueueManager queueManager,
    ILogger<SessionSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);
    private readonly ConcurrentDictionary<string, string> _activeTrackedSessions = new(StringComparer.OrdinalIgnoreCase);

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
        using var scope = scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IQwenPawClient>();

        // Check primary agent 'mons', 'default', and any agent currently having active sessions in queueManager
        var agentsToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mons", "default" };
        foreach (var ag in queueManager.GetActiveAgentSessions().Keys)
        {
            if (!string.IsNullOrWhiteSpace(ag))
            {
                agentsToCheck.Add(ag);
            }
        }
        foreach (var ag in _activeTrackedSessions.Values)
        {
            if (!string.IsNullOrWhiteSpace(ag))
            {
                agentsToCheck.Add(ag);
            }
        }

        var discoveredActiveThisCycle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successfullyCheckedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var agent in agentsToCheck)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var normalizedAgent = FleetInstanceRoutingService.NormalizeAgentId(agent);
                var chatsRaw = await client.GetAgentChatsRawAsync(agent, ct);
                if (string.IsNullOrWhiteSpace(chatsRaw)) continue;

                using var doc = JsonDocument.Parse(chatsRaw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                successfullyCheckedAgents.Add(normalizedAgent);

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                    var status = el.TryGetProperty("status", out var statProp) ? statProp.GetString() : "idle";

                    var isRunning = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase);

                    if (isRunning)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            discoveredActiveThisCycle.Add(id);
                            if (!_activeTrackedSessions.ContainsKey(id))
                            {
                                _activeTrackedSessions[id] = normalizedAgent;
                                queueManager.SetSessionTurnActive(id, true, normalizedAgent);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(sid))
                        {
                            discoveredActiveThisCycle.Add(sid);
                            if (!_activeTrackedSessions.ContainsKey(sid))
                            {
                                _activeTrackedSessions[sid] = normalizedAgent;
                                queueManager.SetSessionTurnActive(sid, true, normalizedAgent);
                            }
                        }
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Ignore transient network hiccups per agent
            }
        }

        // Reconcile only sessions that were discovered by this background service and whose agent was successfully checked
        foreach (var (trackedId, trackedAgent) in _activeTrackedSessions.ToArray())
        {
            if (successfullyCheckedAgents.Contains(trackedAgent) && !discoveredActiveThisCycle.Contains(trackedId))
            {
                _activeTrackedSessions.TryRemove(trackedId, out _);
                queueManager.SetSessionTurnActive(trackedId, false, trackedAgent);
                // Only mark the specific chat UUID as unread, avoid marking shared session identifiers
                // across multiple chat history branches unless it is an explicit chat UUID
                if (!trackedId.StartsWith("telegram:", StringComparison.OrdinalIgnoreCase) &&
                    !trackedId.StartsWith("zalo:", StringComparison.OrdinalIgnoreCase))
                {
                    ChannelResponseStore.SetUnread(trackedId, true);
                }
            }
        }
    }
}
