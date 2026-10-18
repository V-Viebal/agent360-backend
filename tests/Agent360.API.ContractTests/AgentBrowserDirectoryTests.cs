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
        foreach (var agent in new[] { "mons", "ken", "zen", "mkai", "gm-boc", "gm-team", "gm-lua", "gm-gao", "climax-default", "climax-team" })
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
    public void AnchorUrlNamesTheProfile(string url, string? expected) =>
        AgentBrowserDirectory.ProfileOf(url).Should().Be(expected);

    [Fact]
    public void UnmappedRuntimeHasNoBrowser() =>
        Create().TryResolve("llm360-inference-provider", out _, out _, out _, out _, out _).Should().BeFalse();

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
