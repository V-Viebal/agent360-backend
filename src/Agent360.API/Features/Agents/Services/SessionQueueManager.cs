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
    private readonly ConcurrentDictionary<string, bool> _activeSessionTurns = new();

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
        return _activeSessionTurns.TryGetValue(sessionId, out var active) && active;
    }

    public void SetSessionTurnActive(string? sessionId, bool active)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        // Only active turns are tracked; finished sessions are removed so the map cannot grow without bound.
        if (active) _activeSessionTurns[sessionId] = true;
        else _activeSessionTurns.TryRemove(sessionId, out _);
    }

    public IReadOnlyList<string> GetActiveSessionIds() => _activeSessionTurns.Keys.ToList();
}
