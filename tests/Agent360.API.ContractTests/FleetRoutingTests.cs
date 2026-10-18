using System.Net;
using System.Net.Http.Json;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Options;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class FleetRoutingTests
{
    private static FleetInstanceRoutingService Create(Func<HttpRequestMessage, HttpResponseMessage> handler, QwenPawOptions? options = null) =>
        new(new HttpClient(new StubHandler(handler)), NullLogger<FleetInstanceRoutingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new QwenPawOptions()));

    [Theory]
    [InlineData("mons", "core", "mons")]
    [InlineData("bot:agent360:default-core", "core", "default")]
    [InlineData("mons-QwenPaw_QA_Agent_0.2", "core", "QwenPaw_QA_Agent_0.2")]
    [InlineData("gm-boc", "gm", "default")]
    [InlineData("gm-team", "gm", "team")]
    [InlineData("gm-lua", "gm", "customer")]
    [InlineData("gm-gao", "gm", "supplier")]
    [InlineData("climax-default", "climax", "default")]
    [InlineData("climax-team", "climax", "team")]
    [InlineData("planthy-pm", "planthy", "default")]
    [InlineData("planthy-team", "planthy", "team")]
    [InlineData("agentteams-manager", "agentteams", "default")]
    public void ResolveTarget_MapsEveryFleetAgentToItsRuntimeProfile(string agentId, string instance, string profile)
    {
        var target = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)).ResolveTarget(agentId);

        target.IsQwenPaw.Should().BeTrue();
        target.Instance!.Key.Should().Be(instance);
        target.TargetAgentProfile.Should().Be(profile);
        target.Instance.InternalUrl.Should().StartWith(instance == "agentteams" ? "https://" : "http://100.");
    }

    [Theory]
    [InlineData("agentteams-gateway")]
    [InlineData("llm360-inference-provider")]
    [InlineData("tsx-copaw")]
    public void ResolveTarget_FlagsNonQwenPawRuntimes(string agentId)
    {
        Create(_ => new HttpResponseMessage(HttpStatusCode.OK)).ResolveTarget(agentId).IsQwenPaw.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_FailsOverToPublicEndpoint_WhenInternalIsUnreachable()
    {
        var hosts = new List<string>();
        var routing = Create(req =>
        {
            hosts.Add(req.RequestUri!.Host);
            if (req.RequestUri.Host.StartsWith("100.")) throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var core = routing.Instances.Single(i => i.Key == "core");

        using var response = await routing.SendAsync(core, HttpMethod.Get, "agents/mons/agent-status", ct: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        hosts.Should().Equal("100.76.185.111", "ai.hmz.one");
    }

    [Fact]
    public async Task SendAsync_LogsInAndRetriesOnce_WhenTokenIsRejected()
    {
        var logins = 0;
        var calls = 0;
        var options = new QwenPawOptions();
        options.Instances["gm"] = new QwenPawInstanceCredentials { Username = "admin", Password = "secret-value" };
        var routing = Create(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/auth/login")
            {
                logins++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token = $"t{logins}" }) };
            }
            calls++;
            return req.Headers.Authorization?.Parameter == "t2"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }, options);
        var gm = routing.Instances.Single(i => i.Key == "gm");

        using var response = await routing.SendAsync(gm, HttpMethod.Get, "agents/team/agent-status", ct: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        logins.Should().Be(2);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_DoesNotReplayChatPost_WhenRuntimeIsSlow()
    {
        var hosts = new List<string>();
        var routing = Create(req =>
        {
            hosts.Add(req.RequestUri!.Host);
            throw new TaskCanceledException("runtime still thinking");
        });
        var core = routing.Instances.Single(i => i.Key == "core");

        var act = () => routing.SendAsync(core, HttpMethod.Post, "console/chat", () => JsonContent.Create(new { q = 1 }), timeout: TimeSpan.FromSeconds(1), ct: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        hosts.Should().Equal("100.76.185.111");
    }

    [Fact]
    public async Task SendAsync_DoesNotRetrySlowReadOnPublicRoute_SameRuntimeWouldBeHitTwice()
    {
        var hosts = new List<string>();
        var routing = Create(req =>
        {
            hosts.Add(req.RequestUri!.Host);
            throw new TaskCanceledException("slow");
        });
        var core = routing.Instances.Single(i => i.Key == "core");

        var act = () => routing.SendAsync(core, HttpMethod.Get, "agents/mons/chats", timeout: TimeSpan.FromSeconds(1), ct: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        hosts.Should().Equal("100.76.185.111");
    }

    [Fact]
    public async Task SendAsync_FailsOverChatPost_OnlyWhenInternalConnectFails_AndRemembersIt()
    {
        var hosts = new List<string>();
        var state = new FleetRoutingState();
        HttpResponseMessage Handler(HttpRequestMessage req)
        {
            hosts.Add(req.RequestUri!.Host);
            if (req.RequestUri.Host.StartsWith("100."))
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new System.Net.Sockets.SocketException(111));
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        FleetInstanceRoutingService New() => new(new HttpClient(new StubHandler(Handler)), NullLogger<FleetInstanceRoutingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new QwenPawOptions()), state);

        var first = New();
        using var r1 = await first.SendAsync(first.Instances.Single(i => i.Key == "core"), HttpMethod.Post, "console/chat", ct: TestContext.Current.CancellationToken);
        // A second (transient) service instance must reuse the shared failover mark and go straight to public.
        var second = New();
        using var r2 = await second.SendAsync(second.Instances.Single(i => i.Key == "core"), HttpMethod.Post, "console/chat", ct: TestContext.Current.CancellationToken);

        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        hosts.Should().Equal("100.76.185.111", "ai.hmz.one", "ai.hmz.one");
    }

    [Fact]
    public async Task LoginToken_IsSharedAcrossServiceInstances()
    {
        var logins = 0;
        var state = new FleetRoutingState();
        var options = new QwenPawOptions();
        options.Instances["gm"] = new QwenPawInstanceCredentials { Username = "admin", Password = "secret-value" };
        HttpResponseMessage Handler(HttpRequestMessage req)
        {
            if (req.RequestUri!.AbsolutePath == "/api/auth/login")
            {
                logins++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token = "t" }) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        for (var i = 0; i < 3; i++)
        {
            var routing = new FleetInstanceRoutingService(new HttpClient(new StubHandler(Handler)), NullLogger<FleetInstanceRoutingService>.Instance,
                Microsoft.Extensions.Options.Options.Create(options), state);
            using var _ = await routing.SendAsync(routing.Instances.Single(x => x.Key == "gm"), HttpMethod.Get, "agents/team/chats", ct: TestContext.Current.CancellationToken);
        }

        logins.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Locked)]
    public async Task RejectedLogin_IsNotRetriedPerRequest(HttpStatusCode status)
    {
        // QwenPaw locks the admin account after 5 failed logins/minute; a burst of requests with a bad password
        // must produce one login attempt, not one per request.
        var logins = 0;
        var state = new FleetRoutingState();
        var options = new QwenPawOptions();
        options.Instances["gm"] = new QwenPawInstanceCredentials { Username = "admin", Password = "wrong" };
        HttpResponseMessage Handler(HttpRequestMessage req)
        {
            if (req.RequestUri!.AbsolutePath == "/api/auth/login")
            {
                Interlocked.Increment(ref logins);
                return new HttpResponseMessage(status);
            }
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        var routing = new FleetInstanceRoutingService(new HttpClient(new StubHandler(Handler)), NullLogger<FleetInstanceRoutingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options), state);
        var gm = routing.Instances.Single(x => x.Key == "gm");
        for (var i = 0; i < 20; i++)
        {
            using var _ = await routing.SendAsync(gm, HttpMethod.Get, "agents/team/chats", ct: TestContext.Current.CancellationToken);
        }

        logins.Should().Be(1);
        state.LoginBlockedUntil.Should().ContainKey("gm");
    }

    [Fact]
    public void LoginBackoff_KeepsUnderTheLockoutThreshold()
    {
        FleetInstanceRoutingService.LoginBackoff(HttpStatusCode.Unauthorized).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
        FleetInstanceRoutingService.LoginBackoff(HttpStatusCode.Locked).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5));
        FleetInstanceRoutingService.LoginBackoff(HttpStatusCode.BadGateway).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ConnectFailureClassifier_DistinguishesNeverSentFromSlow()
    {
        FleetInstanceRoutingService.IsConnectFailure(new HttpRequestException(HttpRequestError.ConnectionError, "x")).Should().BeTrue();
        FleetInstanceRoutingService.IsConnectFailure(new TaskCanceledException("t", new TimeoutException("A connection could not be established within the configured ConnectTimeout."))).Should().BeTrue();
        FleetInstanceRoutingService.IsConnectFailure(new TaskCanceledException("request timeout")).Should().BeFalse();
        FleetInstanceRoutingService.IsConnectFailure(new HttpRequestException(HttpRequestError.ResponseEnded, "x")).Should().BeFalse();
        // Seen live: a slow read cancelled by our timeout surfaces as SocketException(125 OperationAborted).
        FleetInstanceRoutingService.IsConnectFailure(new TaskCanceledException("c", new IOException("read", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.OperationAborted)))).Should().BeFalse();
        FleetInstanceRoutingService.IsConnectFailure(new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))).Should().BeTrue();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
