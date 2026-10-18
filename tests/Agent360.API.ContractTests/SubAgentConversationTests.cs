using System.Net;
using System.Text.Json;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Multi-turn delegation: child asks the parent (QUESTION_FOR_PARENT), parent answers in the same native session.</summary>
public sealed class SubAgentConversationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a360-conv-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQwenPaw _fake = new();
    private readonly SubAgentStore _store;
    private readonly SubAgentService _svc;
    private readonly Queue<string> _replies = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SubAgentConversationTests()
    {
        var opt = MsOptions.Create(new SubAgentOptions { StoragePath = _dir, MaxConversationTurns = 6, AwaitingParentTimeoutSeconds = 60 });
        _store = new SubAgentStore(opt, new TestEnv(_dir));
        _svc = new SubAgentService(_store, new QwenPawA2AClient(new HttpClient(_fake), MsOptions.Create(new QwenPawOptions { BaseUrl = "http://fake" })),
            opt, NullLogger<SubAgentService>.Instance);
        _fake.Routes["POST /api/console/chat"] = _ => FakeQwenPaw.Sse(_replies.Count > 0 ? _replies.Dequeue() : "DONE");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private async Task<DelegationTask> StartAsync(params string[] childReplies)
    {
        foreach (var r in childReplies) _replies.Enqueue(r);
        await _svc.UpsertBindingAsync("mons", "ken", new UpsertSubAgentBindingRequest("PO", ["requirements"], null, 2, 120, false, true), Ct);
        var r0 = await _svc.CreateAsync(new CreateDelegationRequest("mons", "ken", "sync", "Write the release note title", null, null, null,
            ["contains 'RELEASE'"], null, null, null, null, null), Ct);
        return r0.Value!;
    }

    private static string Session(string body) => JsonDocument.Parse(body).RootElement.GetProperty("session_id").GetString()!;

    private static string Text(string body) => JsonDocument.Parse(body).RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public void ExtractQuestion_ReadsMarkerAndIgnoresNormalAnswers()
    {
        SubAgentPolicy.ExtractQuestion("QUESTION_FOR_PARENT: Which version number?").Should().Be("Which version number?");
        SubAgentPolicy.ExtractQuestion("Some preface\n`QUESTION_FOR_PARENT: Which locale?`").Should().Be("Which locale?");
        SubAgentPolicy.ExtractQuestion("RELEASE 2.2 is ready").Should().BeNull();
        SubAgentPolicy.ExtractQuestion("QUESTION_FOR_PARENT:   ").Should().BeNull();
    }

    [Fact]
    public async Task ChildQuestion_ParksTask_ThenParentAnswerInSameSession_Completes()
    {
        var t = await StartAsync("QUESTION_FOR_PARENT: Which version is released?", "RELEASE 2.2.1 notes");
        t.Status.Should().Be(DelegationStatuses.AwaitingParent);
        t.Turns.Select(x => x.Kind).Should().Equal("request", "question");
        Text(_fake.Calls.Last(c => c.Path == "/api/console/chat").Body).Should().Contain(SubAgentPolicy.QuestionMarker, "the protocol is taught in the first prompt");

        var r = await _svc.SendMessageAsync(t.Id, new DelegationMessageRequest("2.2.1", null), Ct);

        r.Decision.Allowed.Should().BeTrue();
        r.Value!.Status.Should().Be(DelegationStatuses.UnderReview);
        r.Value.Output.Should().Be("RELEASE 2.2.1 notes");
        r.Value.Turns.Select(x => (x.From, x.Kind)).Should().Equal(("mons", "request"), ("ken", "question"), ("mons", "answer"), ("ken", "final"));
        var chats = _fake.Calls.Where(c => c.Path == "/api/console/chat").ToList();
        chats.Should().HaveCount(2);
        Session(chats[1].Body).Should().Be(Session(chats[0].Body), "the answer must continue the child's native session");
        Text(chats[1].Body).Should().Contain("ANSWER_FROM_PARENT: 2.2.1");
        chats.Should().OnlyContain(c => c.Agent == "ken");
    }

    [Fact]
    public async Task FollowUp_AfterResult_ReusesSessionAndReturnsToReview()
    {
        var t = await StartAsync("draft title", "RELEASE final title");
        t.Status.Should().Be(DelegationStatuses.UnderReview);

        var r = await _svc.SendMessageAsync(t.Id, new DelegationMessageRequest("Make it start with RELEASE", null), Ct);

        r.Value!.Turns.Select(x => x.Kind).Should().Equal("request", "final", "message", "final");
        r.Value.Output.Should().Be("RELEASE final title");
        r.Value.Status.Should().Be(DelegationStatuses.UnderReview);
    }

    [Fact]
    public async Task Message_RejectedWhenAcceptedOrBudgetExhaustedOrSecret()
    {
        var t = await StartAsync("QUESTION_FOR_PARENT: a?", "QUESTION_FOR_PARENT: b?", "QUESTION_FOR_PARENT: c?");
        (await _svc.SendMessageAsync(t.Id, new DelegationMessageRequest("password=hunter2xyz", null), Ct)).Decision.StatusCode.Should().Be(422);
        (await _svc.SendMessageAsync(t.Id, new DelegationMessageRequest("one", null), Ct)).Value!.Turns.Should().HaveCount(4);
        (await _svc.SendMessageAsync(t.Id, new DelegationMessageRequest("two", null), Ct)).Value!.Turns.Should().HaveCount(6);
        var over = await _svc.SendMessageAsync(t.Id, new DelegationMessageRequest("three", null), Ct);
        over.Decision.StatusCode.Should().Be(409);
        over.Decision.Reason.Should().Contain("turn budget");

        var t2 = await _svc.CreateAsync(new CreateDelegationRequest("mons", "ken", "sync", "x", null, null, null, [], null, null, null, null, null), Ct);
        await _store.SaveTaskAsync(t2.Value! with { Status = DelegationStatuses.Accepted });
        (await _svc.SendMessageAsync(t2.Value!.Id, new DelegationMessageRequest("more", null), Ct)).Decision.StatusCode.Should().Be(409);
        (await _svc.SendMessageAsync("dlg_missing", new DelegationMessageRequest("x", null), Ct)).Decision.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Monitor_TimesOutUnansweredQuestion()
    {
        var t = await StartAsync("QUESTION_FOR_PARENT: which one?");
        await _store.SaveTaskAsync(t with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });

        (await _svc.MonitorOnceAsync(Ct)).Should().BeGreaterThan(0);

        var after = await _store.GetTaskAsync(t.Id);
        after!.Status.Should().Be(DelegationStatuses.TimedOut);
        after.Error.Should().Contain("did not answer");
    }

    [Fact]
    public async Task Phase4_GroupsNativeCallsOnOneSessionIntoOneConversation()
    {
        await _svc.UpsertBindingAsync("mons", "ken", new UpsertSubAgentBindingRequest("PO", ["requirements"], null, 2, 120, false, true), Ct);
        _fake.Routes["GET /api/chats"] = _ => FakeQwenPaw.Json(new[] { new { id = "chat-9", updated_at = "2026-10-18" } });
        _fake.Routes["GET /api/chats/chat-9"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"messages":[
             {"type":"plugin_call","created_at":"2026-10-18T03:00:00Z","content":[{"type":"data","data":{"call_id":"k1","name":"chat_with_agent","arguments":"{\"to_agent\":\"ken\",\"text\":\"title please\"}"}}]},
             {"type":"plugin_call_output","content":[{"type":"data","data":{"call_id":"k1","name":"chat_with_agent","output":"[SESSION: mons:to:ken:9:aa]\n\nQUESTION_FOR_PARENT: Which version?"}}]},
             {"type":"plugin_call","created_at":"2026-10-18T03:01:00Z","content":[{"type":"data","data":{"call_id":"k2","name":"chat_with_agent","arguments":"{\"to_agent\":\"ken\",\"session_id\":\"mons:to:ken:9:aa\",\"text\":\"ANSWER_FROM_PARENT: 2.2.1\"}"}}]},
             {"type":"plugin_call_output","content":[{"type":"data","data":{"call_id":"k2","name":"chat_with_agent","output":"[SESSION: mons:to:ken:9:aa]\n\nRELEASE 2.2.1"}}]}
            ]}
            """)
        };

        await _svc.IngestNativeAsync("mons", 10, Ct);
        (await _svc.IngestNativeAsync("mons", 10, Ct)).Should().BeEmpty("re-ingest is idempotent");

        var tasks = await _store.GetTasksAsync("mons");
        tasks.Should().ContainSingle();
        var conv = tasks[0];
        conv.Status.Should().Be(DelegationStatuses.Completed);
        conv.Output.Should().Be("RELEASE 2.2.1");
        conv.Turns.Select(x => (x.From, x.Kind)).Should().Equal(("mons", "request"), ("ken", "question"), ("mons", "answer"), ("ken", "final"));
    }

    [Fact]
    public void Skill_TeachesSessionReuseAndClarificationProtocol()
    {
        var skill = SubAgentSkillGenerator.Generate("mons", [new SubAgentBinding("sab_1", "mons", "ken", "core", "subagent", "PO", ["x"], ["sync", "async"], 1, 2, 300, false, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        skill.Should().Contain("QUESTION_FOR_PARENT:").And.Contain("ANSWER_FROM_PARENT:").And.Contain("session_id=<same SESSION id>");
    }
}
