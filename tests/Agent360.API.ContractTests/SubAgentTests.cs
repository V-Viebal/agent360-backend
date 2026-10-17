using System.Net;
using System.Text;
using System.Text.Json;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Fake QwenPaw server: records requests, returns scripted responses keyed by "METHOD path".</summary>
internal sealed class FakeQwenPaw : HttpMessageHandler
{
    public readonly List<(string Method, string Path, string? Agent, string Body)> Calls = [];
    public readonly Dictionary<string, Func<string, HttpResponseMessage>> Routes = new();
    public readonly Dictionary<string, bool> Agents = new() { ["mons"] = true, ["ken"] = true, ["mkai"] = true, ["default"] = false };

    public FakeQwenPaw()
    {
        Routes["GET /api/agents"] = _ => Json(new { agents = Agents.Select(a => new { id = a.Key, name = a.Key, enabled = a.Value }) });
        Routes["POST /api/skills"] = _ => Json(new { created = true });
        Routes["POST /api/console/chat"] = _ => Sse("A2A_SYNC_OK");
        Routes["POST /api/console/chat/task"] = _ => Json(new { task_id = "task-123", timeout = 300 });
        Routes["GET /api/console/chat/task/task-123"] = _ => Json(new
        {
            status = "finished",
            result = new { status = "completed", session_id = "s1", output = new[] { new { content = new[] { new { type = "text", text = "A2A_ASYNC_OK" } } } } }
        });
        Routes["POST /api/console/chat/stop"] = _ => Json(new { stopped = true });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var agent = request.Headers.TryGetValues("X-Agent-Id", out var v) ? v.First() : null;
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add((request.Method.Method, path, agent, body));
        if (agent is not null && path.StartsWith("/api/console/chat") && Agents.TryGetValue(agent, out var en) && !en)
            return Json(new { detail = $"Agent '{agent}' is disabled" }, HttpStatusCode.Forbidden);
        if (agent is not null && path.StartsWith("/api/console/chat") && !Agents.ContainsKey(agent))
            return Json(new { detail = $"Agent '{agent}' not found" }, HttpStatusCode.NotFound);
        return Routes.TryGetValue($"{request.Method.Method} {path}", out var h) ? h(body) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(string text, string status = "completed")
    {
        var final = JsonSerializer.Serialize(new { @object = "response", status, output = new[] { new { content = new[] { new { type = "text", text } } } } });
        var usage = JsonSerializer.Serialize(new { type = "turn_usage" });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"data: {{\"object\":\"response\",\"status\":\"in_progress\"}}\n\ndata: {final}\n\ndata: {usage}\n\n", Encoding.UTF8, "text/event-stream")
        };
    }
}

internal sealed class TestEnv(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public sealed class SubAgentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a360-subagents-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQwenPaw _fake = new();
    private readonly SubAgentService _svc;
    private readonly SubAgentStore _store;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SubAgentTests()
    {
        var opt = MsOptions.Create(new SubAgentOptions { StoragePath = _dir });
        _store = new SubAgentStore(opt, new TestEnv(_dir));
        var client = new QwenPawA2AClient(new HttpClient(_fake), MsOptions.Create(new QwenPawOptions { BaseUrl = "http://fake" }));
        _svc = new SubAgentService(_store, client, opt, NullLogger<SubAgentService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static UpsertSubAgentBindingRequest Bind(params string[] modes) =>
        new("Product Owner", ["requirements", "acceptance-review"], modes.Length == 0 ? null : modes, 2, 120, false, true);

    private static CreateDelegationRequest Dlg(string mode, params string[] criteria) =>
        new("mons", "ken", mode, "Reply with the marker", "smoke", null, null, criteria, null, null, null, null, null);

    // ---------- Unit: binding validation ----------
    [Fact]
    public void Binding_RejectsSelfDelegation() =>
        SubAgentPolicy.ValidateBinding("mons", "mons", Bind()).Allowed.Should().BeFalse();

    [Fact]
    public void Binding_RejectsInvalidMode() =>
        SubAgentPolicy.ValidateBinding("mons", "ken", Bind("parallel")).Allowed.Should().BeFalse();

    [Fact]
    public async Task Binding_RejectsUnknownChildOnRuntime()
    {
        var r = await _svc.UpsertBindingAsync("mons", "ghost", Bind(), Ct);
        r.Decision.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Binding_DisabledRuntimeAgentIsStoredDisabled()
    {
        var r = await _svc.UpsertBindingAsync("mons", "default", Bind(), Ct);
        r.Value!.Enabled.Should().BeFalse();
    }

    // ---------- Unit: delegation policy ----------
    [Fact]
    public async Task Delegation_RejectsSelf()
    {
        var r = await _svc.CreateAsync(Dlg("sync") with { ChildAgentId = "mons" }, Ct);
        r.Decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Delegation_RejectsUnboundChild()
    {
        var r = await _svc.CreateAsync(Dlg("sync") with { ChildAgentId = "mkai" }, Ct);
        r.Decision.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Delegation_RejectsCycleAndDepth()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        var cycle = await _svc.CreateAsync(Dlg("sync") with { CallChain = ["ken", "mons"] }, Ct);
        cycle.Decision.Allowed.Should().BeFalse();
        cycle.Decision.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Delegation_RejectsSecretsInPayload()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        var r = await _svc.CreateAsync(Dlg("sync") with { Context = "use token: ghp_abcdefghijklmnopqrstuvwxyz123456" }, Ct);
        r.Decision.StatusCode.Should().Be(422);
        _fake.Calls.Should().NotContain(c => c.Path == "/api/console/chat");
    }

    [Fact]
    public async Task Delegation_RejectsDisallowedMode()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind("sync"), Ct);
        (await _svc.CreateAsync(Dlg("async"), Ct)).Decision.StatusCode.Should().Be(403);
    }

    // ---------- Unit: skill generator ----------
    [Fact]
    public async Task Skill_ListsOnlyEnabledBindingsAndIsSynced()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        var skillCall = _fake.Calls.Last(c => c.Path == "/api/skills");
        skillCall.Agent.Should().Be("mons");
        skillCall.Body.Should().Contain("agent360-subagents").And.Contain("| ken | Product Owner");

        await _svc.DeleteBindingAsync("mons", "ken", Ct);
        _fake.Routes["POST /api/skills"] = _ => FakeQwenPaw.Json(new { }, HttpStatusCode.Conflict);
        _fake.Routes["PUT /api/skills/save"] = _ => FakeQwenPaw.Json(new { success = true });
        _fake.Routes["POST /api/skills/agent360-subagents/enable"] = _ => FakeQwenPaw.Json(new { });
        var (synced, content) = await _svc.SyncSkillAsync("mons", Ct);
        synced.Should().BeTrue();
        content.Should().Contain("No sub-agents are currently bound").And.NotContain("| ken |");
    }

    // ---------- Unit: acceptance ----------
    [Fact]
    public void Acceptance_CannotAcceptWhenRequiredCriterionFails()
    {
        string[] criteria = ["Output contains 'A2A_OK'"];
        var checks = SubAgentPolicy.EvaluateRules(criteria, "something else");
        var d = SubAgentPolicy.Decide("t", "human", criteria, checks, "accepted", null);
        d.Decision.Should().Be("rejected");
    }

    [Fact]
    public void Acceptance_CannotAcceptWithUnevaluatedCriterion()
    {
        string[] criteria = ["Explains the trade-offs clearly"];
        var d = SubAgentPolicy.Decide("t", "human", criteria, [], "accepted", null);
        d.Decision.Should().Be("rejected");
    }

    [Fact]
    public void Acceptance_MinItemsRule()
    {
        string[] criteria = ["Có tối thiểu 3 tiêu chí kiểm thử"];
        SubAgentPolicy.EvaluateRules(criteria, "1. a\n2. b\n3. c")[0].Result.Should().Be("pass");
        SubAgentPolicy.EvaluateRules(criteria, "1. a")[0].Result.Should().Be("fail");
    }

    [Fact]
    public void StatusTransitions_AreEnforced()
    {
        DelegationStatuses.CanTransition("completed", "accepted").Should().BeTrue();
        DelegationStatuses.CanTransition("rejected", "retry_queued").Should().BeTrue();
        DelegationStatuses.CanTransition("accepted", "running").Should().BeFalse();
        DelegationStatuses.CanTransition("queued", "accepted").Should().BeFalse();
    }

    // ---------- Contract: fake QwenPaw ----------
    [Fact]
    public async Task Contract_SyncChatSucceeds_AndUsesNativeContract()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        var r = await _svc.CreateAsync(Dlg("sync", "Output contains 'A2A_SYNC_OK'"), Ct);
        r.Value!.Status.Should().Be("under_review");
        r.Value.Output.Should().Be("A2A_SYNC_OK");
        r.Value.Acceptance!.Decision.Should().Be("accepted");

        var call = _fake.Calls.Single(c => c.Path == "/api/console/chat");
        call.Agent.Should().Be("ken");
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("user_id").GetString().Should().Be("mons");
        body.RootElement.GetProperty("session_id").GetString().Should().StartWith("mons:to:ken:");
        body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString().Should().StartWith("[Agent mons requesting]");

        var accepted = await _svc.ReviewAsync(r.Value.Id, new("accepted", "rule-based", null, null));
        accepted.Value!.Status.Should().Be("accepted");
    }

    [Fact]
    public async Task Contract_AsyncSubmitThenCheckCompleted()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        var r = await _svc.CreateAsync(Dlg("async"), Ct);
        r.Value!.Status.Should().Be("running");
        r.Value.NativeTaskId.Should().Be("task-123");
        var synced = await _svc.SyncNativeStatusAsync(r.Value.Id, Ct);
        synced!.Status.Should().Be("completed");
        synced.Output.Should().Be("A2A_ASYNC_OK");
    }

    [Fact]
    public async Task Contract_CheckTaskRunningAndFailed()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        var r = await _svc.CreateAsync(Dlg("async"), Ct);
        _fake.Routes["GET /api/console/chat/task/task-123"] = _ => FakeQwenPaw.Json(new { status = "running", started_at = "x" });
        (await _svc.SyncNativeStatusAsync(r.Value!.Id, Ct))!.Status.Should().Be("running");
        _fake.Routes["GET /api/console/chat/task/task-123"] = _ => FakeQwenPaw.Json(new { status = "finished", result = new { status = "failed", error = new { message = "boom" } } });
        var failed = await _svc.SyncNativeStatusAsync(r.Value.Id, Ct);
        failed!.Status.Should().Be("failed");
        failed.Error.Should().Be("boom");
    }

    [Fact]
    public async Task Contract_TargetDisabledIsFailed()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        _fake.Agents["ken"] = false;
        var r = await _svc.CreateAsync(Dlg("sync"), Ct);
        r.Value!.Status.Should().Be("failed");
        r.Value.Error.Should().Contain("disabled");
    }

    [Fact]
    public async Task Contract_SyncTimeoutStopsNativeChat()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        _fake.Routes["POST /api/console/chat"] = _ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(12));
            return FakeQwenPaw.Sse("late");
        };
        var r = await _svc.CreateAsync(Dlg("sync") with { TimeoutSeconds = 10 }, Ct);
        r.Value!.Status.Should().Be("timed_out");
        _fake.Calls.Should().Contain(c => c.Path == "/api/console/chat/stop" && c.Agent == "ken");
    }

    [Fact]
    public async Task Contract_InvalidSchemaIsFailedNotCrash()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        _fake.Routes["POST /api/console/chat"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {not-json}\n\ndata: {\"foo\":1}\n\n") };
        var r = await _svc.CreateAsync(Dlg("sync"), Ct);
        r.Value!.Status.Should().Be("failed");
    }

    [Fact]
    public async Task Review_RejectThenRetryWithFeedback_MaxOnce()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        _fake.Routes["POST /api/console/chat"] = _ => FakeQwenPaw.Sse("wrong answer");
        var r = await _svc.CreateAsync(Dlg("sync", "Output contains 'A2A_SYNC_OK'"), Ct);
        var rejected = await _svc.ReviewAsync(r.Value!.Id, new(null, "rule-based", null, null));
        rejected.Value!.Status.Should().Be("rejected");

        _fake.Routes["POST /api/console/chat"] = _ => FakeQwenPaw.Sse("A2A_SYNC_OK");
        var retried = await _svc.RetryAsync(r.Value.Id, "You must output the exact marker A2A_SYNC_OK", Ct);
        retried.Value!.Attempt.Should().Be(2);
        retried.Value.Status.Should().Be("under_review");
        _fake.Calls.Last(c => c.Path == "/api/console/chat").Body.Should().Contain("Reviewer feedback");

        var rejectAgain = await _svc.ReviewAsync(r.Value.Id, new("rejected", "human", [new("Output contains 'A2A_SYNC_OK'", "pass", "ok")], "test"));
        rejectAgain.Value!.Status.Should().Be("rejected");
        (await _svc.RetryAsync(r.Value.Id, "again", Ct)).Decision.StatusCode.Should().Be(409);
    }

    [Fact]
    public void Phase4_ExtractsNativeToolCallsFromChatHistory()
    {
        const string history = """
        {"messages":[
          {"role":"user","type":"message","content":[{"type":"text","text":"ask ken"}]},
          {"role":"assistant","type":"plugin_call","created_at":"2026-10-17T03:57:00Z","content":[{"type":"data","data":{"call_id":"call-1","name":"chat_with_agent","arguments":"{\"to_agent\":\"ken\",\"text\":\"Reply A2A_SPIKE_OK\"}"}}]},
          {"role":"assistant","type":"plugin_call_output","content":[{"type":"data","data":{"call_id":"call-1","name":"chat_with_agent","output":"[{\"type\": \"text\", \"text\": \"[SESSION: mons:to:ken:1:abc]\\n\\nA2A_SPIKE_OK\"}]"}}]},
          {"role":"assistant","type":"plugin_call","content":[{"type":"data","data":{"call_id":"call-2","name":"submit_to_agent","arguments":"{\"to_agent\":\"mkai\",\"text\":\"long job\"}"}}]},
          {"role":"assistant","type":"plugin_call_output","content":[{"type":"data","data":{"call_id":"call-2","name":"submit_to_agent","output":"[TASK_ID: task-9]\n[SESSION: mons:to:mkai:2:def]"}}]},
          {"role":"assistant","type":"plugin_call","content":[{"type":"data","data":{"call_id":"call-3","name":"read_file","arguments":"{}"}}]}
        ]}
        """;
        var d = QwenPawA2AClient.ExtractDelegations("mons", "chat-1", history);
        d.Should().HaveCount(2);
        var sync = d.Single(x => x.CallId == "call-1");
        sync.ChildAgentId.Should().Be("ken");
        sync.NativeSessionId.Should().Be("mons:to:ken:1:abc");
        sync.Output.Should().Contain("A2A_SPIKE_OK");
        d.Single(x => x.CallId == "call-2").NativeTaskId.Should().Be("task-9");
    }

    [Fact]
    public async Task Phase4_IngestIsIdempotentAndFlagsUnboundChild()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        _fake.Routes["GET /api/chats"] = _ => FakeQwenPaw.Json(new[] { new { id = "chat-1", updated_at = "2026-10-17" } });
        _fake.Routes["GET /api/chats/chat-1"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"messages":[{"type":"plugin_call","content":[{"type":"data","data":{"call_id":"c1","name":"chat_with_agent","arguments":"{\"to_agent\":\"ken\",\"text\":\"hi\"}"}}]},{"type":"plugin_call_output","content":[{"type":"data","data":{"call_id":"c1","name":"chat_with_agent","output":"[SESSION: s]\n\nhello"}}]},{"type":"plugin_call","content":[{"type":"data","data":{"call_id":"c2","name":"chat_with_agent","arguments":"{\"to_agent\":\"zen\",\"text\":\"hi\"}"}}]}]}""")
        };
        var first = await _svc.IngestNativeAsync("mons", 10, Ct);
        first.Should().HaveCount(2);
        first.Single(t => t.ChildAgentId == "ken").Status.Should().Be("completed");
        first.Single(t => t.ChildAgentId == "zen").Evidence.Should().Contain("policy_violation:unbound_child");
        (await _svc.IngestNativeAsync("mons", 10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Store_PersistsAcrossInstances()
    {
        await _svc.UpsertBindingAsync("mons", "ken", Bind(), Ct);
        var reopened = new SubAgentStore(MsOptions.Create(new SubAgentOptions { StoragePath = _dir }), new TestEnv(_dir));
        (await reopened.GetBindingsAsync("mons")).Should().ContainSingle(b => b.ChildAgentId == "ken");
    }
}
