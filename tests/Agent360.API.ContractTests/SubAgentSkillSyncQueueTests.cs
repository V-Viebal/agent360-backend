using System.Net;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class SubAgentSkillSyncQueueTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a360-skillq-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQwenPaw _fake = new();
    private ServiceProvider _sp = null!;
    private SubAgentSkillSyncQueue _queue = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IHostEnvironment>(new TestEnv(_dir));
        services.AddSingleton(MsOptions.Create(new SubAgentOptions { StoragePath = _dir }));
        services.AddSingleton<SubAgentStore>();
        services.AddSingleton<IQwenPawA2AClient>(new QwenPawA2AClient(new HttpClient(_fake), MsOptions.Create(new QwenPawOptions { BaseUrl = "http://fake" })));
        services.AddSingleton<SubAgentSkillSyncQueue>();
        services.AddSingleton<ISubAgentSkillSyncQueue>(sp => sp.GetRequiredService<SubAgentSkillSyncQueue>());
        services.AddScoped<SubAgentService>();
        _sp = services.BuildServiceProvider();
        _queue = _sp.GetRequiredService<SubAgentSkillSyncQueue>();
        _queue.RetryBackoff = TimeSpan.FromMilliseconds(10);
        await _queue.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.StopAsync(CancellationToken.None);
        await _sp.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private async Task<SkillSyncStatus> WaitFor(string parent, params string[] states)
    {
        for (var i = 0; i < 200; i++)
        {
            var s = _queue.GetStatus(parent);
            if (states.Contains(s.State)) return s;
            await Task.Delay(25, Ct);
        }
        return _queue.GetStatus(parent);
    }

    private static UpsertSubAgentBindingRequest Bind() =>
        new("Product Owner", ["requirements"], null, 2, 120, false, true);

    [Fact]
    public async Task BindingChange_ReturnsWithoutWaitingForSlowRuntime_ThenSyncsInBackground()
    {
        var gate = new TaskCompletionSource();
        _fake.Routes["POST /api/skills"] = _ => { gate.Task.Wait(TimeSpan.FromSeconds(10)); return FakeQwenPaw.Json(new { created = true }); };
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<SubAgentService>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        sw.Stop();

        r.Decision.Allowed.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
        _queue.GetStatus("mons").State.Should().BeOneOf("pending", "syncing");

        gate.SetResult();
        var done = await WaitFor("mons", "synced", "failed");
        done.State.Should().Be("synced");
        done.SyncedAt.Should().NotBeNull();
        _fake.Calls.Should().Contain(c => c.Path == "/api/skills" && c.Agent == "mons" && c.Body.Contains("| ken |"));
    }

    [Fact]
    public async Task UnbindAfterBind_FinalSkillReflectsLatestBindings()
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<SubAgentService>();
        await svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        await svc.DeleteBindingAsync("mons", "ken", Ct);

        (await WaitFor("mons", "synced", "failed")).State.Should().Be("synced");
        _fake.Calls.Last(c => c.Path == "/api/skills").Body.Should().Contain("No sub-agents are currently bound");
    }

    [Fact]
    public async Task RuntimeFailure_IsRetriedThenReportedAsFailed()
    {
        _fake.Routes["POST /api/skills"] = _ => FakeQwenPaw.Json(new { detail = "boom" }, HttpStatusCode.InternalServerError);
        _queue.Enqueue("mons");
        var s = await WaitFor("mons", "synced", "failed");
        s.State.Should().Be("failed");
        s.Error.Should().NotBeNullOrWhiteSpace();
        _fake.Calls.Count(c => c.Path == "/api/skills").Should().Be(3);
    }

    [Fact]
    public async Task Sync_InvalidatesSkillCaches()
    {
        var cache = _sp.GetRequiredService<IMemoryCache>();
        cache.Set("agent360:proxy:mons:skills", "stale");
        cache.Set("agent360:skills:mons", "stale");
        _queue.Enqueue("mons");
        (await WaitFor("mons", "synced", "failed")).State.Should().Be("synced");
        cache.TryGetValue("agent360:proxy:mons:skills", out _).Should().BeFalse();
        cache.TryGetValue("agent360:skills:mons", out _).Should().BeFalse();
    }
}
