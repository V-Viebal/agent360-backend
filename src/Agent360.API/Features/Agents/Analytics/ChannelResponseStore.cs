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
    DateTime RecordedAt);

public static class ChannelResponseStore
{
    private static readonly ConcurrentQueue<ChannelResponseEvent> _records = new();

    public static void Record(ChannelResponseEvent record)
    {
        _records.Enqueue(record);
        while (_records.Count > 1000)
        {
            _records.TryDequeue(out _);
        }
    }

    public static ChannelResponseEvent Record(
        string agentId,
        string sessionId,
        string channel,
        string role,
        string content,
        string status = "completed",
        string? id = null)
    {
        var ev = new ChannelResponseEvent(
            Id: id ?? Guid.NewGuid().ToString("N"),
            AgentId: agentId,
            SessionId: sessionId,
            Channel: channel,
            Role: role,
            Content: content,
            Status: status,
            RecordedAt: DateTime.UtcNow
        );
        Record(ev);
        return ev;
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
