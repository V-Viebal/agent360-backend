using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Agent360.API.Features.Agents.Services;

public sealed class QueuedChatTask
{
    public string TaskId { get; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public DateTime QueuedAt { get; } = DateTime.UtcNow;
    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class SessionQueueManager
{
    private readonly ConcurrentDictionary<string, Channel<QueuedChatTask>> _sessionChannels = new();
    private readonly ConcurrentDictionary<string, bool> _activeSessionTurns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionAgentMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, bool>? OnSessionTurnChanged;
    public event Action<string, bool, string?>? OnSessionTurnChangedWithAgent;

    public SemaphoreSlim GetOrCreateSessionLock(string sessionId)
    {
        return _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    }

    public Channel<QueuedChatTask> GetOrCreateSessionChannel(string sessionId)
    {
        return _sessionChannels.GetOrAdd(sessionId, _ => Channel.CreateUnbounded<QueuedChatTask>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        }));
    }

    public bool IsSessionTurnActive(string sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId) && _activeSessionTurns.TryGetValue(sessionId, out var active) && active;
    }

    public string? GetSessionAgent(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return _sessionAgentMap.TryGetValue(sessionId, out var agentId) ? agentId : null;
    }

    public void SetSessionTurnActive(string? sessionId, bool active, string? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        string? normalizedAgent = null;
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            normalizedAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId);
            _sessionAgentMap[sessionId] = normalizedAgent;
        }
        else if (_sessionAgentMap.TryGetValue(sessionId, out var existingAgent))
        {
            normalizedAgent = existingAgent;
        }

        // Only active turns are tracked; finished sessions are removed so the map cannot grow without bound.
        bool changed;
        if (active)
        {
            changed = !_activeSessionTurns.TryGetValue(sessionId, out var cur) || !cur;
            _activeSessionTurns[sessionId] = true;
        }
        else
        {
            changed = _activeSessionTurns.TryRemove(sessionId, out _);
        }

        if (!changed) return;

        try
        {
            OnSessionTurnChanged?.Invoke(sessionId, active);
        }
        catch {}

        try
        {
            OnSessionTurnChangedWithAgent?.Invoke(sessionId, active, normalizedAgent);
        }
        catch {}
    }

    public IReadOnlyList<string> GetActiveSessionIds() => _activeSessionTurns.Keys.ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetActiveAgentSessions()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sessionId in _activeSessionTurns.Keys)
        {
            if (_sessionAgentMap.TryGetValue(sessionId, out var ag) && !string.IsNullOrWhiteSpace(ag))
            {
                if (!result.TryGetValue(ag, out var list))
                {
                    list = new List<string>();
                    result[ag] = list;
                }
                if (!list.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(sessionId);
                }
            }
        }
        return result.ToDictionary(k => k.Key, v => (IReadOnlyList<string>)v.Value, StringComparer.OrdinalIgnoreCase);
    }
}
