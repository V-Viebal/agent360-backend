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
    public void ResolveTarget_MapsEveryFleetAgentToItsRuntimeProfile(string agentId, string instance, string profile)
    {
        var target = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)).ResolveTarget(agentId);

        target.IsQwenPaw.Should().BeTrue();
        target.Instance!.Key.Should().Be(instance);
        target.TargetAgentProfile.Should().Be(profile);
        target.Instance.InternalUrl.Should().StartWith("http://100.");
    }

    [Theory]
    [InlineData("agentteams-manager")]
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
            if (req.RequestUri.Host.StartsWith("100.")) throw new HttpRequestException("connection refused");
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
