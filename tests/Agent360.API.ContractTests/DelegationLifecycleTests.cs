using System.Net;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Agent360.API.ContractTests;

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public sealed class DelegationLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a360-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQwenPaw _fake = new();
    private readonly SubAgentStore _store;
    private readonly SubAgentService _svc;
    private readonly SubAgentOptions _opt;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DelegationLifecycleTests()
    {
        _opt = new SubAgentOptions { StoragePath = _dir, TimeoutGraceSeconds = 0 };
        var opt = MsOptions.Create(_opt);
        _store = new SubAgentStore(opt, new TestEnv(_dir));
        var client = new QwenPawA2AClient(new HttpClient(_fake), MsOptions.Create(new QwenPawOptions { BaseUrl = "http://fake" }));
        _svc = new SubAgentService(_store, client, opt, NullLogger<SubAgentService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private Task Bind(int timeout = 120) =>
        _svc.UpsertBindingAsync("mons", "ken", new("PO", ["requirements"], null, 2, timeout, false, true), Ct);

    private static CreateDelegationRequest Dlg(string mode, params string[] criteria) =>
        new("mons", "ken", mode, "Reply with the marker", "smoke", null, null, criteria, null, null, null, null, null);

    // ---------- Operator auth ----------
    private static HttpContext Http(string method, string? bearer = null, string? header = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        if (bearer is not null) ctx.Request.Headers.Authorization = "Bearer " + bearer;
        if (header is not null) ctx.Request.Headers[OperatorAuthFilter.HeaderName] = header;
        return ctx;
    }

    private static readonly IHostEnvironment Prod = new TestEnv("/") { EnvironmentName = "Production" };
    private static readonly IHostEnvironment Dev = new TestEnv("/") { EnvironmentName = "Development" };

    [Fact]
    public void OperatorAuth_RejectsMissingOrWrongToken() =>
        new[] { Http("POST"), Http("POST", "wrong"), Http("PUT", header: "nope") }
            .Should().OnlyContain(c => !OperatorAuthFilter.IsAuthorized(c, new Agent360SecurityOptions { OperatorToken = "s3cret" }, Prod));

    [Fact]
    public void OperatorAuth_AcceptsBearerOrHeader()
    {
        var o = new Agent360SecurityOptions { OperatorToken = "s3cret" };
        OperatorAuthFilter.IsAuthorized(Http("POST", "s3cret"), o, Prod).Should().BeTrue();
        OperatorAuthFilter.IsAuthorized(Http("DELETE", header: "s3cret"), o, Prod).Should().BeTrue();
    }

    [Fact]
    public void OperatorAuth_FailsClosedWithoutConfiguredTokenOutsideDevelopment()
    {
        OperatorAuthFilter.IsAuthorized(Http("POST", "anything"), new Agent360SecurityOptions(), Prod).Should().BeFalse();
        OperatorAuthFilter.IsAuthorized(Http("POST"), new Agent360SecurityOptions(), Dev).Should().BeTrue();
    }

    [Fact]
    public async Task OperatorFilter_AllowsReadsAndBlocksAnonymousWrites()
    {
        var filter = new OperatorAuthFilter(new StaticOptionsMonitor<Agent360SecurityOptions>(new() { OperatorToken = "s3cret" }), Prod);
        var reached = 0;
        EndpointFilterDelegate next = _ => { reached++; return ValueTask.FromResult<object?>("ok"); };

        (await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(Http("GET")), next)).Should().Be("ok");
        var denied = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(Http("POST")), next);
        denied.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(401);
        (await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(Http("POST", "s3cret")), next)).Should().Be("ok");
        reached.Should().Be(2);
    }

    // ---------- Input validation ----------
    [Fact]
    public void CreateShape_RejectsEmptyAndMalformedBodies()
    {
        SubAgentPolicy.ValidateCreateShape(null).StatusCode.Should().Be(400);
        SubAgentPolicy.ValidateCreateShape(new(null!, null!, null!, null!, null, null, null, null, null, null, null, null, null)).Allowed.Should().BeFalse();
        SubAgentPolicy.ValidateCreateShape(Dlg("stream")).Reason.Should().Contain("mode");
        SubAgentPolicy.ValidateCreateShape(Dlg("sync") with { TimeoutSeconds = 5 }).Allowed.Should().BeFalse();
        SubAgentPolicy.ValidateCreateShape(Dlg("async")).Allowed.Should().BeTrue();
    }

    // ---------- Retry contract ----------
    [Fact]
    public async Task Retry_RevisedContractReplacesOriginalInPrompt()
    {
        await Bind();
        var t = (await _svc.CreateAsync(Dlg("sync", "Output contains 'A2A_SYNC_OK'"), Ct)).Value!;
        await _svc.ReviewAsync(t.Id, new("rejected", "human", null, "force"));

        _fake.Routes["POST /api/console/chat"] = _ => FakeQwenPaw.Sse("RETRY_OK");
        var r = await _svc.RetryAsync(t.Id, new RetryDelegationRequest("Use the new marker", "Reply with RETRY_OK", "RETRY_OK", ["Output contains 'RETRY_OK'"]), Ct);

        r.Decision.Allowed.Should().BeTrue();
        r.Value!.Attempt.Should().Be(2);
        r.Value.Objective.Should().Be("Reply with RETRY_OK");
        r.Value.AcceptanceCriteria.Should().ContainSingle("Output contains 'RETRY_OK'");
        r.Value.Status.Should().Be("under_review");
        r.Value.Acceptance!.Checks.Should().OnlyContain(c => c.Result == "pass");
        var prompt = _fake.Calls.Last(c => c.Path == "/api/console/chat").Body;
        prompt.Should().Contain("Reply with RETRY_OK").And.Contain("feedback takes precedence").And.NotContain("A2A_SYNC_OK");
    }

    [Fact]
    public async Task Retry_RejectsSecretsInFeedback()
    {
        await Bind();
        var t = (await _svc.CreateAsync(Dlg("sync"), Ct)).Value!;
        await _svc.ReviewAsync(t.Id, new("rejected", "human", null, "x"));
        (await _svc.RetryAsync(t.Id, new RetryDelegationRequest("use api_key=sk-abcdefghijklmnopqrstuvwxyz123456"), Ct)).Decision.StatusCode.Should().Be(422);
    }

    // ---------- Monitor ----------
    [Fact]
    public async Task Monitor_CompletesRunningAsyncTaskFromNativeStatus()
    {
        await Bind();
        var t = (await _svc.CreateAsync(Dlg("async", "Output contains 'A2A_ASYNC_OK'"), Ct)).Value!;
        t.Status.Should().Be("running");
        (await _svc.MonitorOnceAsync(Ct)).Should().Be(1);
        (await _store.GetTaskAsync(t.Id))!.Status.Should().Be("under_review");
    }

    [Fact]
    public async Task Monitor_TimesOutStaleRunningTask()
    {
        await Bind(timeout: 10);
        _fake.Routes["GET /api/console/chat/task/task-123"] = _ => FakeQwenPaw.Json(new { status = "running" });
        var t = (await _svc.CreateAsync(Dlg("async"), Ct)).Value!;
        var stale = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _store.SaveTaskAsync((await _store.GetTaskAsync(t.Id))! with { CreatedAt = stale, UpdatedAt = stale });

        // The native poll itself refreshes UpdatedAt; age it again to simulate a task stuck for longer than the limit.
        _fake.Routes["GET /api/console/chat/task/task-123"] = _ => throw new HttpRequestException("down");
        (await _svc.MonitorOnceAsync(Ct)).Should().Be(1);
        var done = (await _store.GetTaskAsync(t.Id))!;
        done.Status.Should().Be("timed_out");
        done.Evidence.Should().Contain(e => e.StartsWith("monitor:timed_out"));
    }

    [Fact]
    public async Task BoundParents_OnlyEnabledBindings()
    {
        await Bind();
        await _svc.UpsertBindingAsync("zen", "mkai", new("x", [], null, 1, 60, false, false), Ct);
        (await _svc.BoundParentsAsync()).Should().BeEquivalentTo(["mons"]);
    }

    // ---------- Retention ----------
    [Fact]
    public async Task Compact_ArchivesOldTerminalTasksAndKeepsActive()
    {
        await Bind();
        var done = (await _svc.CreateAsync(Dlg("sync"), Ct)).Value!;
        await _svc.ReviewAsync(done.Id, new("accepted", "human", null, "ok"));
        var running = (await _svc.CreateAsync(Dlg("async"), Ct)).Value!;
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        foreach (var id in new[] { done.Id, running.Id })
            await _store.SaveTaskAsync((await _store.GetTaskAsync(id))! with { CreatedAt = old, UpdatedAt = old });

        (await _store.CompactAsync(TimeSpan.FromDays(30), 2000)).Should().Be(1);
        (await _store.GetTaskAsync(done.Id)).Should().BeNull();
        (await _store.GetTaskAsync(running.Id)).Should().NotBeNull();
        var archive = Directory.GetFiles(_dir, "delegation_tasks.archive-*.jsonl").Should().ContainSingle().Subject;
        File.ReadAllText(archive).Should().Contain(done.Id);
    }

    [Fact]
    public async Task Compact_EnforcesMaxTasksCap()
    {
        await Bind();
        for (var i = 0; i < 105; i++)
        {
            var t = new DelegationTask($"dlg_{i:D4}", "r", "r", null, 1, ["mons", "ken"], "mons", "ken", "sync", "agent360", null, "", "o", null, [],
                "accepted", 1, 1, "x", null, [], null, [], DateTimeOffset.UtcNow.AddMinutes(-i), DateTimeOffset.UtcNow.AddMinutes(-i), DateTimeOffset.UtcNow);
            await _store.SaveTaskAsync(t);
        }
        (await _store.CompactAsync(TimeSpan.FromDays(30), 100)).Should().Be(5);
        (await _store.GetTasksAsync()).Should().HaveCount(100);
        (await _store.GetTaskAsync("dlg_0104")).Should().BeNull();
    }
}

public sealed class RuntimeUnavailableExceptionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Microsoft.AspNetCore.Http.DefaultHttpContext Http, MemoryStream Body) Ctx()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        http.Request.Path = "/api/v1/agents/mons/chats";
        return (http, body);
    }

    [Fact]
    public async Task RuntimeUnreachable_Maps503WithRetryAfter()
    {
        var (http, body) = Ctx();
        var h = new Agent360.API.Features.Agents.Services.RuntimeUnavailableExceptionHandler(NullLogger<Agent360.API.Features.Agents.Services.RuntimeUnavailableExceptionHandler>.Instance);
        (await h.TryHandleAsync(http, new HttpRequestException("QwenPaw instance 'core' is unreachable", new TaskCanceledException()), Ct)).Should().BeTrue();
        http.Response.StatusCode.Should().Be(503);
        http.Response.Headers.RetryAfter.ToString().Should().Be("5");
        System.Text.Encoding.UTF8.GetString(body.ToArray()).Should().Contain("\"retryable\":true");
    }

    [Fact]
    public async Task OtherExceptions_AreLeftToGlobalHandler()
    {
        var (http, _) = Ctx();
        var h = new Agent360.API.Features.Agents.Services.RuntimeUnavailableExceptionHandler(NullLogger<Agent360.API.Features.Agents.Services.RuntimeUnavailableExceptionHandler>.Instance);
        (await h.TryHandleAsync(http, new InvalidOperationException("bug"), Ct)).Should().BeFalse();
        (await h.TryHandleAsync(http, new NullReferenceException(), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task ClientAbort_IsSwallowed()
    {
        var (http, _) = Ctx();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        http.RequestAborted = cts.Token;
        var h = new Agent360.API.Features.Agents.Services.RuntimeUnavailableExceptionHandler(NullLogger<Agent360.API.Features.Agents.Services.RuntimeUnavailableExceptionHandler>.Instance);
        (await h.TryHandleAsync(http, new OperationCanceledException(), Ct)).Should().BeTrue();
        http.Response.StatusCode.Should().Be(499);
    }
}

public sealed class RuntimeProxyOperatorGuardTests
{
    [Fact]
    public void ProxyGuard_RequiresTokenOnlyForWritesWhenEnabled()
    {
        var env = new TestEnv("/") { EnvironmentName = "Production" };
        var opt = new Agent360SecurityOptions { OperatorToken = "s3cret", ProtectRuntimeProxyWrites = true };
        var post = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        post.Request.Method = "POST";
        OperatorAuthFilter.IsAuthorized(post, opt, env).Should().BeFalse();
        post.Request.Headers[OperatorAuthFilter.HeaderName] = "s3cret";
        OperatorAuthFilter.IsAuthorized(post, opt, env).Should().BeTrue();
    }
}
