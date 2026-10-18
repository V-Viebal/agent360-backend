using Microsoft.Extensions.Configuration;
using Agent360.API.Features.Agents.Browser;
using Agent360.API.Features.Agents.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class AgentBrowserDirectoryTests
{
    private static AgentBrowserDirectory Create() => new(
        new FleetInstanceRoutingService(new HttpClient(), NullLogger<FleetInstanceRoutingService>.Instance),
        new NoHttpFactory(),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<AgentBrowserDirectory>.Instance);

    [Theory]
    [InlineData("mons", "chromium-browser-q4yu", "Default", "http://100.76.185.111:19222")]
    [InlineData("ken", "chromium-browser-q4yu", "Profile 1", "http://100.76.185.111:19222")]
    [InlineData("zen", "chromium-browser-q4yu", "Profile 2", "http://100.76.185.111:19222")]
    [InlineData("mkai", "chromium-browser-q4yu", "Profile 3", "http://100.76.185.111:19222")]
    [InlineData("gm-boc", "browser-gm-21er", "Default", "http://100.81.66.42:19222")]
    [InlineData("gm-team", "browser-gm-21er", "Profile 1", "http://100.81.66.42:19222")]
    [InlineData("gm-lua", "browser-gm-21er", "Profile 2", "http://100.81.66.42:19222")]
    [InlineData("gm-gao", "browser-gm-21er", "Profile 3", "http://100.81.66.42:19222")]
    [InlineData("climax-default", "climax-browser", "Default", "http://100.70.201.83:19222")]
    [InlineData("climax-team", "climax-browser", "Profile 2", "http://100.70.201.83:19222")]
    [InlineData("planthy-pm", "planthy-browser-rff7", "Default", "http://100.70.201.83:19224")]
    [InlineData("planthy-team", "planthy-browser-rff7", "Profile 1", "http://100.70.201.83:19224")]
    public void EveryAgentResolvesToItsOwnBrowserAndProfile(string agent, string service, string profile, string cdp)
    {
        Create().TryResolve(agent, out _, out var svc, out _, out var cdpUrl, out var prof).Should().BeTrue();
        svc.Should().Be(service);
        prof.Should().Be(profile);
        cdpUrl.Should().Be(cdp);
    }

    [Fact]
    public void ProfilesAreOneToOnePerBrowser()
    {
        var dir = Create();
        var seen = new Dictionary<string, string>();
        foreach (var agent in new[] { "mons", "ken", "zen", "mkai", "gm-boc", "gm-team", "gm-lua", "gm-gao", "climax-default", "climax-team", "planthy-pm", "planthy-team" })
        {
            dir.TryResolve(agent, out _, out var svc, out _, out _, out var prof);
            seen.TryAdd($"{svc}/{prof}", agent).Should().BeTrue($"{svc}/{prof} must not be shared ({agent})");
        }
    }

    [Fact]
    public void MonsNoLongerRoutesToTheGmBrowser()
    {
        var routing = new FleetInstanceRoutingService(new HttpClient(), NullLogger<FleetInstanceRoutingService>.Instance);
        routing.ResolveTarget("mons").CdpUrl.Should().StartWith("http://100.76.185.111");
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/agent360-profile?profile=Profile%202", "Profile 2")]
    [InlineData("http://127.0.0.1:3000/agent360-profile?profile=Default", "Default")]
    [InlineData("http://127.0.0.1:3000/agent360-profile?profile=Profile+3&x=1", "Profile 3")]
    [InlineData("https://example.com/?profile=Default", null)]
    [InlineData("data:text/html;charset=utf-8,<!--agent360-profile=Profile%202--><title>Agent profile: Profile%202</title>", "Profile 2")]
    [InlineData("data:text/html;charset=utf-8,<!--agent360-profile=Default--><body>x</body>", "Default")]
    [InlineData("data:text/html,<title>x</title>", null)]
    public void AnchorUrlNamesTheProfile(string url, string? expected) =>
        AgentBrowserDirectory.ProfileOf(url).Should().Be(expected);

    [Fact]
    public void UnmappedRuntimeHasNoBrowser() =>
        Create().TryResolve("llm360-inference-provider", out _, out _, out _, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("http://100.76.185.111:19222", true)]
    [InlineData("http://10.0.1.7:9222", true)]
    [InlineData("http://192.168.1.5:9222", true)]
    [InlineData("http://browser-gm-21er:9223", true)]
    [InlineData("http://cdp.internal:9222", true)]
    [InlineData("https://cdp-browser.hmz.one", false)]
    [InlineData("http://8.8.8.8:9222", false)]
    [InlineData("http://user:pw@100.76.185.111:19222", false)]
    [InlineData("file:///etc/passwd", false)]
    public void OnlyInternalCdpEndpointsAreAccepted(string url, bool ok) =>
        AgentBrowserDirectory.IsInternalEndpoint(url).Should().Be(ok);

    [Fact]
    public void PublicCdpOverrideIsIgnoredInternalOverrideIsUsed()
    {
        static AgentBrowserDirectory With(string url) => new(
            new FleetInstanceRoutingService(new HttpClient(), NullLogger<FleetInstanceRoutingService>.Instance),
            new NoHttpFactory(), new MemoryCache(new MemoryCacheOptions()), NullLogger<AgentBrowserDirectory>.Instance,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions
            {
                Instances = { ["core"] = new Agent360.API.Options.QwenPawInstanceCredentials { BrowserCdpUrl = url } }
            }));
        With("https://cdp-browser.hmz.one").TryResolve("mons", out _, out _, out _, out var cdp1, out _);
        cdp1.Should().Be("http://100.76.185.111:19222");
        With("http://100.76.185.111:19333/").TryResolve("mons", out _, out _, out _, out var cdp2, out _);
        cdp2.Should().Be("http://100.76.185.111:19333");
    }

    [Fact]
    public void BrowserInfoNeverSerializesInternalEndpoint()
    {
        var info = new AgentBrowserInfo("mons", "core", "3rd", "svc", "https://browser.hmz.one", "http://100.76.185.111:19222",
            "Default", "CTX-SECRET", true, true, [new AgentBrowserTab("T1", "t", "https://x", false)]);
        var json = System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().NotContain("100.76").And.NotContain("CTX-SECRET").And.NotContain("cdpUrl").And.NotContain("ws://");
        json.Should().Contain("\"viewerUrl\"");
    }

    [Fact]
    public async Task GateCoalescesConcurrentProbes()
    {
        var gate = new AgentBrowserGate();
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<int> Probe() { Interlocked.Increment(ref calls); await release.Task; return 42; }
        var tasks = Enumerable.Range(0, 10).Select(_ => gate.Coalesce("k", Probe)).ToArray();
        release.SetResult();
        (await Task.WhenAll(tasks)).Should().AllBeEquivalentTo(42);
        calls.Should().Be(1);
    }

    [Fact]
    public void StreamSlotsAreCappedPerInstance()
    {
        var gate = new AgentBrowserGate { MaxStreamsPerInstance = 2 };
        var s = gate.StreamSlots("core");
        s.Wait(0, TestContext.Current.CancellationToken).Should().BeTrue();
        s.Wait(0, TestContext.Current.CancellationToken).Should().BeTrue();
        s.Wait(0, TestContext.Current.CancellationToken).Should().BeFalse();
        gate.StreamSlots("gm").Wait(0, TestContext.Current.CancellationToken).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://agent360.antares.vn", "api-agent360.antares.vn", true)]
    [InlineData("https://agent360.hmz.one", "agent360.hmz.one", true)]
    [InlineData("https://evil.example", "api-agent360.antares.vn", false)]
    [InlineData(null, "api-agent360.antares.vn", false)]
    public void StreamOriginIsRestricted(string? origin, string host, bool ok)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        AgentBrowserCdpEndpoint.IsAllowedOrigin(ctx, ["https://agent360.antares.vn"]).Should().Be(ok);
    }

    [Fact]
    public void CorsOriginsFromOneEnvValueAreSplit()
    {
        var scalar = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins"] = "https://a.example;https://b.example, http://localhost:5173"
        }).Build();
        Agent360.API.Options.CorsOptions.Read(scalar).Should().Equal("https://a.example", "https://b.example", "http://localhost:5173");
        var array = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://a.example", ["Cors:AllowedOrigins:1"] = "https://b.example"
        }).Build();
        Agent360.API.Options.CorsOptions.Read(array).Should().Equal("https://a.example", "https://b.example");
    }

    [Fact]
    public void DefaultTabSkipsChromeInternalPages()
    {
        var anchor = new AgentBrowserTab("A", "", "data:text/html;charset=utf-8,<!--agent360-profile=Default-->", true);
        var newtab = new AgentBrowserTab("N", "New Tab", "chrome://newtab/", false);
        var web = new AgentBrowserTab("W", "Site", "https://example.com/", false);
        AgentBrowserCdpEndpoint.DefaultTab([anchor, newtab]).Should().Be(anchor);
        AgentBrowserCdpEndpoint.DefaultTab([anchor, newtab, web]).Should().Be(web);
        AgentBrowserCdpEndpoint.DefaultTab([newtab]).Should().BeNull();
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
