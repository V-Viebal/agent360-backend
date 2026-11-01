using Agent360.API.Features.Agents.Browser;
using Agent360.API.Features.Agents.SubAgents;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class BrowserViewerAccessTests
{
    private static BrowserViewerAccess Access(string secret = "op-secret") =>
        new(new StaticMonitor(new Agent360SecurityOptions { OperatorToken = secret }));

    [Fact]
    public void TicketRoundTripsForSameHostAndKind()
    {
        var a = Access();
        var now = DateTimeOffset.UtcNow;
        var t = a.Sign("ticket", "browser.hmz.one", "watch", now.AddSeconds(60));
        a.Verify(t, "ticket", "browser.hmz.one", now).Should().Be("watch");
        a.Verify(t, "ticket", "BROWSER.hmz.one", now).Should().Be("watch");
    }

    [Fact]
    public void RejectsOtherHostKindExpiryTamperAndSecret()
    {
        var a = Access();
        var now = DateTimeOffset.UtcNow;
        var t = a.Sign("ticket", "browser.hmz.one", "control", now.AddSeconds(60));
        a.Verify(t, "ticket", "browser-gm.antares.vn", now).Should().BeNull();
        a.Verify(t, "session", "browser.hmz.one", now).Should().BeNull();
        a.Verify(t, "ticket", "browser.hmz.one", now.AddSeconds(61)).Should().BeNull();
        a.Verify(t[..^2] + "AA", "ticket", "browser.hmz.one", now).Should().BeNull();
        Access("other").Verify(t, "ticket", "browser.hmz.one", now).Should().BeNull();
        a.Verify("garbage", "ticket", "browser.hmz.one", now).Should().BeNull();
        a.Verify(null, "ticket", "browser.hmz.one", now).Should().BeNull();
    }

    [Fact]
    public void PayloadModeCannotBeUpgradedWithoutResigning()
    {
        var a = Access();
        var now = DateTimeOffset.UtcNow;
        var t = a.Sign("session", "browser.hmz.one", "watch", now.AddHours(1));
        var parts = t.Split('.');
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"session|browser.hmz.one|control|{now.AddHours(1).ToUnixTimeSeconds()}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + parts[1];
        a.Verify(forged, "session", "browser.hmz.one", now).Should().BeNull();
    }

    [Theory]
    [InlineData("/a360/abc.def/", "abc.def")]
    [InlineData("/a360/abc.def/assets/index.js?x=1", "abc.def")]
    [InlineData("/a360/abc.def", "abc.def")]
    [InlineData("/a360/abc.def/websockets", "abc.def")]
    [InlineData("/assets/index.js", null)]
    [InlineData("/", null)]
    [InlineData(null, null)]
    public void TokenComesOnlyFromTheA360Prefix(string? uri, string? expected) =>
        BrowserViewerAccess.TokenFromUri(uri).Should().Be(expected);

    [Fact]
    public void EmptySecretNeverVerifies()
    {
        var signed = Access().Sign("session", "browser.hmz.one", "control", DateTimeOffset.UtcNow.AddHours(1));
        Access("").Verify(signed, "session", "browser.hmz.one", DateTimeOffset.UtcNow).Should().BeNull();
        Access("").CanSign.Should().BeFalse();
    }

    [Fact]
    public void ViewerIssueIsGuardedByTheEndpointNotTheOperatorOnlyMiddleware() =>
        // The endpoint authorizes operator key OR signed-in admin itself; the operator-only middleware would 401 admins.
        Agent360.API.Features.Agents.Security.OperatorWriteGuardMiddleware.IsProtected("agents/mons/browser/viewer").Should().BeFalse();

    private sealed class StaticMonitor(Agent360SecurityOptions value) : IOptionsMonitor<Agent360SecurityOptions>
    {
        public Agent360SecurityOptions CurrentValue => value;
        public Agent360SecurityOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<Agent360SecurityOptions, string?> listener) => null;
    }
}

public sealed class BrowserProfileMapParsingTests
{
    [Theory]
    [InlineData("mons-app360", "app360", "Profile 6")]
    [InlineData("app360", "app360", "Profile 6")]
    [InlineData("ken", "ken", "Profile 1")]
    public void ResolvesDedicatedCoreBrowserProfileForApp360AndPeers(string slug, string expectedTargetProfile, string expectedBrowserProfile)
    {
        var routing = new Agent360.API.Features.Agents.Services.FleetInstanceRoutingService(
            new System.Net.Http.HttpClient(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Services.FleetInstanceRoutingService>.Instance);
        var dir = new Agent360.API.Features.Agents.Browser.AgentBrowserDirectory(
            routing,
            new StubHttpClientFactory(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Browser.AgentBrowserDirectory>.Instance);

        dir.TryResolve(slug, out var target, out var service, out var viewerUrl, out _, out var profile).Should().BeTrue();
        target.TargetAgentProfile.Should().Be(expectedTargetProfile);
        service.Should().Be("chromium-browser-q4yu");
        viewerUrl.Should().Be("https://browser.hmz.one");
        profile.Should().Be(expectedBrowserProfile);
    }

    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public void BuildAnchorUrlIncludesStyledDarkThemeBodyAndProfileMarker()
    {
        var url = Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.BuildAnchorUrl("Default", "mons");
        url.Should().StartWith(Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.AnchorPrefix);
        url.Should().Contain("<body");
        url.Should().Contain("background:%230d0d12");
        Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.ProfileOf(url).Should().Be("Default");
    }

    [Fact]
    public void ParsesPlainAndBase64JsonAndRejectsGarbage()
    {
        const string json = "{\"mons\":\"Default\",\"ken\":\"Profile 1\"}";
        var plain = Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.ParseProfileMap(json);
        plain!["MONS"].Should().Be("Default");
        var b64 = Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.ParseProfileMap(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));
        b64!["ken"].Should().Be("Profile 1");
        Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.ParseProfileMap("not-json").Should().BeNull();
        Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.ParseProfileMap("{}").Should().BeNull();
        Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.ParseProfileMap(null).Should().BeNull();
    }
}
