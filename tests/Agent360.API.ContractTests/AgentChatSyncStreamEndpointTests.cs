using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Endpoints;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using AwesomeAssertions;
using Xunit;

namespace Agent360.API.ContractTests;

public class AgentChatSyncStreamEndpointTests
{
    [Fact]
    public void Endpoint_ImplementsIEndpoint_AndHasRoute()
    {
        var endpoint = new AgentChatSyncStreamEndpoint();
        endpoint.Should().NotBeNull();
        AgentChatSyncStreamEndpoint.RouteName.Should().Be("StreamAgentChatSessionSync");
    }

    [Fact]
    public void ChannelResponseStore_RecordsAndFiltersEvents()
    {
        var record = new ChannelResponseEvent(
            Id: Guid.NewGuid().ToString(),
            AgentId: "mons",
            SessionId: "telegram:1448508743",
            Channel: "telegram",
            Role: "assistant",
            Content: "Em chao anh!",
            Status: "completed",
            RecordedAt: DateTime.UtcNow);

        ChannelResponseStore.Record(record);

        var recent = ChannelResponseStore.GetRecent("mons", "telegram", 10);
        recent.Should().NotBeEmpty();
        recent[0].AgentId.Should().Be("mons");
        recent[0].Channel.Should().Be("telegram");
        recent[0].SessionId.Should().Be("telegram:1448508743");
        recent[0].Content.Should().Be("Em chao anh!");

        var bySession = ChannelResponseStore.GetRecentBySession("telegram:1448508743", 5);
        bySession.Should().NotBeEmpty();
        bySession[0].SessionId.Should().Be("telegram:1448508743");
    }

    [Fact]
    public void ChannelResponseStore_TracksUnreadStateCorrectly()
    {
        const string sid = "telegram:unread_test_123";
        ChannelResponseStore.MarkRead(sid);
        ChannelResponseStore.IsUnread(sid).Should().BeFalse();

        // Recording completed assistant response marks session unread
        ChannelResponseStore.Record("mons", sid, "telegram", "assistant", "Turn finished", "completed");
        ChannelResponseStore.IsUnread(sid).Should().BeTrue();
        ChannelResponseStore.GetUnreadSessionIds().Should().Contain(sid);

        // Marking as read clears unread state
        ChannelResponseStore.MarkRead(sid);
        ChannelResponseStore.IsUnread(sid).Should().BeFalse();
        ChannelResponseStore.GetUnreadSessionIds().Should().NotContain(sid);
    }

    [Fact]
    public void AgentSessionResponse_PreservesChannelAndSessionId_AndIsRunning()
    {
        var resp = new AgentSessionResponse(
            Id: "bb0a89a6-ceeb-491a-a19d-ad52a8f4bb25",
            Title: "Telegram Session",
            UpdatedAt: DateTime.UtcNow.ToString("O"),
            MessageCount: 5,
            LastMessageSnippet: "Hello",
            Unread: true,
            SessionId: "telegram:1448508743",
            Channel: "telegram",
            Status: "running",
            IsRunning: true);

        resp.Id.Should().Be("bb0a89a6-ceeb-491a-a19d-ad52a8f4bb25");
        resp.SessionId.Should().Be("telegram:1448508743");
        resp.Channel.Should().Be("telegram");
        resp.Status.Should().Be("running");
        resp.IsRunning.Should().BeTrue();
        resp.Unread.Should().BeTrue();
    }

    [Fact]
    public void SessionQueueManager_TracksActiveSessionTurns()
    {
        var manager = new SessionQueueManager();
        const string sid = "bb0a89a6-ceeb-491a-a19d-ad52a8f4bb25";

        manager.IsSessionTurnActive(sid).Should().BeFalse();
        manager.SetSessionTurnActive(sid, true);
        manager.IsSessionTurnActive(sid).Should().BeTrue();
        manager.GetActiveSessionIds().Should().Contain(sid);

        manager.SetSessionTurnActive(sid, false);
        manager.IsSessionTurnActive(sid).Should().BeFalse();
        manager.GetActiveSessionIds().Should().NotContain(sid);
    }
}
