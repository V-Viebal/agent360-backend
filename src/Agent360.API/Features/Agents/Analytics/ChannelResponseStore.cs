using System.Collections.Concurrent;

namespace Agent360.API.Features.Agents.Analytics;

public sealed record ChannelResponseEvent(
    string Id,
    string AgentId,
    string SessionId,
    string Channel,
    string Role,
    string Content,
    string Status,
    DateTime RecordedAt,
    string? Tool = null,
    string? Arguments = null,
    string? Result = null,
    string? Metadata = null,
    string? ChatId = null);

public static class ChannelResponseStore
{
    private static readonly ConcurrentQueue<ChannelResponseEvent> _records = new();
    private static readonly ConcurrentDictionary<string, bool> _unreadSessions = new(StringComparer.OrdinalIgnoreCase);

    public static event Action<ChannelResponseEvent>? OnChannelResponseRecorded;
    public static event Action<string, bool>? OnUnreadChanged;

    public static void Record(ChannelResponseEvent record)
    {
        _records.Enqueue(record);
        while (_records.Count > 1000)
        {
            _records.TryDequeue(out _);
        }

        // If turn completed from assistant or external channel, mark session unread until viewed
        if (string.Equals(record.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            SetUnread(record.SessionId, true);
        }

        try
        {
            OnChannelResponseRecorded?.Invoke(record);
        }
        catch {}
    }

    public static ChannelResponseEvent Record(
        string agentId,
        string sessionId,
        string channel,
        string role,
        string content,
        string status = "completed",
        string? id = null,
        string? tool = null,
        string? arguments = null,
        string? result = null,
        string? metadata = null,
        string? chatId = null)
    {
        var ev = new ChannelResponseEvent(
            Id: id ?? Guid.NewGuid().ToString("N"),
            AgentId: agentId,
            SessionId: sessionId,
            Channel: channel,
            Role: role,
            Content: content,
            Status: status,
            RecordedAt: DateTime.UtcNow,
            Tool: tool,
            Arguments: arguments,
            Result: result,
            Metadata: metadata,
            ChatId: chatId
        );
        Record(ev);
        return ev;
    }

    public static void SetUnread(string sessionId, bool isUnread)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        bool changed;
        if (isUnread)
        {
            changed = !_unreadSessions.TryGetValue(sessionId, out var cur) || !cur;
            _unreadSessions[sessionId] = true;
        }
        else
        {
            changed = _unreadSessions.TryRemove(sessionId, out _);
        }

        if (!changed) return;

        try
        {
            OnUnreadChanged?.Invoke(sessionId, isUnread);
        }
        catch {}
    }

    public static bool IsUnread(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        return _unreadSessions.TryGetValue(sessionId, out var unread) && unread;
    }

    public static IReadOnlyList<string> GetUnreadSessionIds()
    {
        return _unreadSessions.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
    }

    public static void MarkRead(string sessionId)
    {
        SetUnread(sessionId, false);
    }

    public static IReadOnlyList<ChannelResponseEvent> GetRecent(string? agentId = null, string? channel = null, int limit = 50)
    {
        var query = _records.Reverse().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            query = query.Where(r => string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(channel))
        {
            query = query.Where(r => string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase));
        }
        return query.Take(limit).ToList();
    }

    public static IReadOnlyList<ChannelResponseEvent> GetRecentBySession(string sessionId, int limit = 50)
    {
        return _records.Reverse()
            .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }
}
