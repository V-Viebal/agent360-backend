using System.Net;
using System.Net.WebSockets;
using Agent360.API.Features.Agents.Browser;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Hosts the real agent browser endpoints on Kestrel; the browser CDP itself is unreachable here.</summary>
public sealed class AgentBrowserEndpointHostTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private Uri _base = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Configuration["Cors:AllowedOrigins:0"] = "https://agent360.antares.vn";
        // Point every instance at a name that never resolves so the test never dials the fleet.
        foreach (var k in new[] { "core", "gm", "climax" })
        {
            b.Configuration[$"QwenPaw:Instances:{k}:BrowserCdpUrl"] = "http://a360-test-no-such-browser:9";
        }
        b.Services.AddRouting();
        b.Services.AddApiVersioning(o => o.DefaultApiVersion = new Asp.Versioning.ApiVersion(1));
        b.Services.AddMemoryCache();
        b.Services.AddOptions<QwenPawOptions>().BindConfiguration(QwenPawOptions.Section);
        b.Services.AddOptions<CorsOptions>().BindConfiguration(CorsOptions.Section);
        b.Services.AddSingleton<FleetRoutingState>();
        b.Services.AddHttpClient<IFleetInstanceRoutingService, FleetInstanceRoutingService>();
        b.Services.AddHttpClient(AgentBrowserDirectory.HttpClientName);
        b.Services.AddSingleton<AgentBrowserGate>();
        b.Services.AddScoped<AgentBrowserDirectory>();
        b.Services.AddRateLimiter(o => o.AddFixedWindowLimiter(AgentBrowserCdpEndpoint.RateLimitPolicy, l => { l.PermitLimit = 1000; l.Window = TimeSpan.FromMinutes(1); }));
        _app = b.Build();
        _app.UseWebSockets();
        _app.UseRateLimiter();
        var api = _app.NewVersionedApi().MapGroup("api/v{version:apiVersion}").HasApiVersion(1);
        new AgentBrowserCdpEndpoint().MapEndpoint(api);
        await _app.StartAsync(Ct);
        _base = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<HttpStatusCode> HandshakeAsync(string agent, string? origin)
    {
        using var ws = new ClientWebSocket();
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{_base.Authority}/api/v1/agents/{agent}/browser/stream"), Ct);
            return HttpStatusCode.SwitchingProtocols;
        }
        catch (WebSocketException)
        {
            return ws.HttpStatusCode;
        }
    }

    [Fact]
    public async Task StreamRouteIsMountedUnderTheVersionedApi()
    {
        // Allowed origin reaches the handler (404 = the agent's browser is unreachable in this host, not "route missing").
        using var http = new HttpClient { BaseAddress = _base };
        (await HandshakeAsync("mons", "https://agent360.antares.vn")).Should().Be(HttpStatusCode.NotFound);
        var body = await (await http.GetAsync("api/v1/agents/mons/browser/pages", Ct)).Content.ReadAsStringAsync(Ct);
        body.Should().NotContain("a360-test-no-such-browser").And.NotContain("ws://");
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData(null)]
    public async Task StreamRejectsForeignOrMissingOrigin(string? origin) =>
        (await HandshakeAsync("mons", origin)).Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task BrowserIdentityHidesInternalEndpoint()
    {
        using var http = new HttpClient { BaseAddress = _base };
        var res = await http.GetAsync("api/v1/agents/gm-boc/browser", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadAsStringAsync(Ct);
        json.Should().Contain("browser-gm-21er").And.NotContain("a360-test-no-such-browser").And.NotContain("cdpUrl").And.NotContain("browserContextId");
    }
}
