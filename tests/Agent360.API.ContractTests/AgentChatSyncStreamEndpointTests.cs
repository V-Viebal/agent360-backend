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

    [Fact]
    public async Task GetChatHistoryRawAsync_CoalescesConcurrentReads_AndServesLastGoodOnTransientTimeout()
    {
        var calls = 0;
        var shouldFail = false;
        var chatId = Guid.NewGuid().ToString();
        var mockHandler = new DelegatingMockHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("chats/" + chatId) == true)
            {
                Interlocked.Increment(ref calls);
                if (shouldFail)
                {
                    throw new TaskCanceledException("simulated single-worker saturation");
                }
                Thread.Sleep(40);
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("{\"messages\":[{\"id\":\"m1\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":\"ok\"}],\"status\":\"idle\"}", System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new System.Net.Http.HttpClient(mockHandler);
        var routing = new FleetInstanceRoutingService(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<FleetInstanceRoutingService>.Instance);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var client = new QwenPawClient(
            httpClient,
            routing,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QwenPawClient>.Instance,
            cache);
        var ct = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetChatHistoryRawAsync(chatId, "mons", ct)));
        calls.Should().Be(1, "concurrent reads within 1.5s TTL must share one upstream request");
        results.Should().AllSatisfy(r => r.Should().Contain("\"m1\""));

        // Expire short fresh cache and simulate transient upstream timeout -> should serve last-good snapshot
        cache.Remove($"agent360:chat-history:mons:{chatId.ToLowerInvariant()}");
        shouldFail = true;
        var fallback = await client.GetChatHistoryRawAsync(chatId, "mons", ct);
        fallback.Should().Contain("\"m1\"");
    }

    private sealed class DelegatingMockHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> handler) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
