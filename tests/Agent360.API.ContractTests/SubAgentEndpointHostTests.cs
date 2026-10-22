using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Hosts the real sub-agent endpoints on Kestrel (random port) against the fake QwenPaw runtime.</summary>
public sealed class SubAgentEndpointHostTests : IAsyncLifetime
{
    private const string Token = "test-operator-token";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a360-host-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQwenPaw _fake = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Configuration["Agent360:Security:OperatorToken"] = Token;
        b.Configuration["Agent360:Security:RequireOperatorForWrites"] = "true";
        b.Configuration["Agent360:SubAgents:StoragePath"] = _dir;
        b.Configuration["Agent360:SubAgents:SyncWaitSeconds"] = "1";
        b.Services.AddRouting();
        b.Services.AddApiVersioning(o => o.DefaultApiVersion = new Asp.Versioning.ApiVersion(1));
        b.Services.AddMemoryCache();
        b.Services.AddOptions<SubAgentOptions>().BindConfiguration(SubAgentOptions.Section);
        b.Services.AddOptions<Agent360SecurityOptions>().BindConfiguration(Agent360SecurityOptions.Section);
        b.Services.AddSingleton<SubAgentStore>();
        b.Services.AddScoped<SubAgentService>();
        b.Services.AddScoped<OperatorAuthFilter>();
        b.Services.AddSingleton<SubAgentSkillSyncQueue>();
        b.Services.AddSingleton<ISubAgentSkillSyncQueue>(sp => sp.GetRequiredService<SubAgentSkillSyncQueue>());
        b.Services.AddHostedService(sp => sp.GetRequiredService<SubAgentSkillSyncQueue>());
        b.Services.AddSingleton<IQwenPawA2AClient>(new QwenPawA2AClient(new HttpClient(_fake), MsOptions.Create(new QwenPawOptions { BaseUrl = "http://fake" })));
        _app = b.Build();
        var api = _app.NewVersionedApi().MapGroup("api/v{version:apiVersion}").HasApiVersion(1);
        new SubAgentEndpoints().MapEndpoint(api);
        await _app.StartAsync(Ct);
        var addr = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(addr + "/api/v1/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null, bool auth = true)
    {
        var r = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (auth) r.Headers.Authorization = new("Bearer", Token);
        return r;
    }

    private static object Dlg(string mode) => new { parent_agent_id = "mons", child_agent_id = "ken", mode, objective = "Reply with the marker" };

    [Fact]
    public async Task Writes_RequireOperatorToken_ReadsDoNot()
    {
        (await _http.SendAsync(Req(HttpMethod.Put, "agents/mons/subagents/ken", new { }, auth: false), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Req(HttpMethod.Post, "delegations", Dlg("sync"), auth: false), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Req(HttpMethod.Get, "agents/mons/subagents", auth: false), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _http.SendAsync(Req(HttpMethod.Put, "agents/mons/subagents/ken", new { }), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EmptyOrMalformedBody_Returns400()
    {
        (await _http.SendAsync(Req(HttpMethod.Post, "delegations", new { }), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var bad = Req(HttpMethod.Post, "delegations");
        bad.Content = new StringContent("{bad", System.Text.Encoding.UTF8, "application/json");
        (await _http.SendAsync(bad, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SlowSyncTurn_Returns202ThenCompletesInBackground()
    {
        (await _http.SendAsync(Req(HttpMethod.Put, "agents/mons/subagents/ken", new { }), Ct)).EnsureSuccessStatusCode();
        _fake.Routes["POST /api/console/chat"] = _ => { Thread.Sleep(2500); return FakeQwenPaw.Sse("SLOW_OK"); };

        var resp = await _http.SendAsync(Req(HttpMethod.Post, "delegations", Dlg("sync")), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString();
        resp.Headers.Location!.ToString().Should().EndWith($"/delegations/{id}");

        string? status = null, output = null;
        for (var i = 0; i < 40 && status is not ("completed" or "under_review"); i++)
        {
            await Task.Delay(150, Ct);
            var doc = JsonDocument.Parse(await _http.GetStringAsync($"delegations/{id}", Ct)).RootElement;
            status = doc.GetProperty("status").GetString();
            output = doc.TryGetProperty("output", out var o) ? o.GetString() : null;
        }
        status.Should().BeOneOf("completed", "under_review");
        output.Should().Be("SLOW_OK");
    }

    [Fact]
    public async Task FastSyncTurn_Returns200WithResult()
    {
        (await _http.SendAsync(Req(HttpMethod.Put, "agents/mons/subagents/ken", new { }), Ct)).EnsureSuccessStatusCode();
        var resp = await _http.SendAsync(Req(HttpMethod.Post, "delegations", Dlg("sync")), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("output").GetString().Should().Be("A2A_SYNC_OK");
    }
}
