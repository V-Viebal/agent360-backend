using System.Net;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Options;
using Microsoft.Extensions.Caching.Memory;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Fails the first N requests for a route with a connect-level error, then delegates to the fake.</summary>
internal sealed class FlakyHandler(FakeQwenPaw inner, string route, int failures, HttpRequestError error = HttpRequestError.ConnectionError) : DelegatingHandler(inner)
{
    public int Remaining = failures;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if ($"{request.Method.Method} {request.RequestUri!.AbsolutePath}" == route && Remaining-- > 0)
            throw new HttpRequestException(error, "simulated transient failure");
        return base.SendAsync(request, ct);
    }
}

public sealed class QwenPawA2AResilienceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static QwenPawA2AResilienceTests() => QwenPawA2AClient.TransientRetryDelay = TimeSpan.Zero;

    private static QwenPawA2AClient Client(HttpMessageHandler h, IMemoryCache? cache = null) =>
        new(new HttpClient(h), MsOptions.Create(new QwenPawOptions { BaseUrl = "http://fake" }), cache);

    [Fact]
    public async Task Submit_RetriesOnceWhenConnectionFails()
    {
        var fake = new FakeQwenPaw();
        var r = await Client(new FlakyHandler(fake, "POST /api/console/chat/task", 1)).SubmitAsync("mons", "ken", "hi", 60, Ct);
        r.Success.Should().BeTrue();
        r.NativeTaskId.Should().Be("task-123");
        fake.Calls.Count(c => c.Path == "/api/console/chat/task").Should().Be(1);
    }

    [Fact]
    public async Task Chat_RetriesOnceWhenConnectionFails()
    {
        var fake = new FakeQwenPaw();
        var r = await Client(new FlakyHandler(fake, "POST /api/console/chat", 1)).ChatAsync("mons", "ken", "hi", 60, Ct);
        r.Success.Should().BeTrue();
        r.Text.Should().Be("A2A_SYNC_OK");
    }

    [Fact]
    public async Task Submit_DoesNotRetryNonConnectFailures()
    {
        var fake = new FakeQwenPaw();
        var act = () => Client(new FlakyHandler(fake, "POST /api/console/chat/task", 1, HttpRequestError.ResponseEnded)).SubmitAsync("mons", "ken", "hi", 60, Ct);
        await act.Should().ThrowAsync<HttpRequestException>();
        fake.Calls.Should().NotContain(c => c.Path == "/api/console/chat/task");
    }

    [Fact]
    public async Task TaskStatus_RetriesOnceOnTransportError()
    {
        var fake = new FakeQwenPaw();
        var st = await Client(new FlakyHandler(fake, "GET /api/console/chat/task/task-123", 1, HttpRequestError.ResponseEnded)).GetTaskAsync("task-123", "ken", Ct);
        st.Status.Should().Be("finished");
        st.Text.Should().Be("A2A_ASYNC_OK");
    }

    [Fact]
    public async Task ListAgents_ServesCacheAndLastGoodWhenRuntimeDown()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var fake = new FakeQwenPaw();
        var flaky = new FlakyHandler(fake, "GET /api/agents", 0);
        var client = Client(flaky, cache);
        (await client.ListAgentsAsync(Ct)).Should().Contain(a => a.Id == "ken");
        (await client.ListAgentsAsync(Ct)).Should().Contain(a => a.Id == "ken");
        fake.Calls.Count(c => c.Path == "/api/agents").Should().Be(1);

        cache.Remove("agent360:a2a:native-agents");
        flaky.Remaining = 10;
        (await client.ListAgentsAsync(Ct)).Should().Contain(a => a.Id == "ken");
    }
}
