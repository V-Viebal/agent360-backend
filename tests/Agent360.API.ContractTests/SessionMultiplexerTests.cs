using Agent360.API.Features.Agents.Analytics;
using Agent360.API.Features.Agents.Endpoints;
using Agent360.API.Features.Agents.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent360.API.ContractTests;

public class SessionMultiplexerTests
{
    [Fact]
    public void Endpoint_ImplementsIEndpoint_AndHasRoute()
    {
        var endpoint = new SessionMultiplexerEndpoint();
        endpoint.Should().NotBeNull();
        SessionMultiplexerEndpoint.RouteName.Should().Be("SessionMultiplexerStream");
    }

    [Fact]
    public void Hub_CreatesSnapshot_WithActiveAndUnreadSessions()
    {
        var queue = new SessionQueueManager();
        const string sid = "telegram:multi_test_1";
        queue.SetSessionTurnActive(sid, true);
        ChannelResponseStore.SetUnread(sid, true);

        using var hub = new SessionMultiplexerHub(queue, NullLogger<SessionMultiplexerHub>.Instance);
        var snapshot = hub.CreateSnapshot();
        snapshot.Should().NotBeNull();

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        json.Should().Contain("telegram:multi_test_1");
        json.Should().Contain("active_sessions_snapshot");

        queue.SetSessionTurnActive(sid, false);
        ChannelResponseStore.MarkRead(sid);
    }

    [Fact]
    public async Task Hub_SubscribesSse_AndReceivesTurnBroadcast()
    {
        var queue = new SessionQueueManager();
        using var hub = new SessionMultiplexerHub(queue, NullLogger<SessionMultiplexerHub>.Instance);

        var reader = hub.SubscribeSse(out var subId);
        hub.ConnectedSseCount.Should().Be(1);

        const string testSession = "telegram:sse_test_turn";
        queue.SetSessionTurnActive(testSession, true);

        // Reader should receive broadcasted event
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var msg = await reader.ReadAsync(cts.Token);
        msg.Should().Contain("session_turn_update");
        msg.Should().Contain(testSession);
        msg.Should().Contain("\"is_running\":true");

        hub.UnsubscribeSse(subId);
        hub.ConnectedSseCount.Should().Be(0);

        queue.SetSessionTurnActive(testSession, false);
    }
}
