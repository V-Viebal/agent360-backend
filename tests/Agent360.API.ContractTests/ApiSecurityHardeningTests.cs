using System.Text.Json.Nodes;
using Agent360.API.Features.Agents.Security;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class ApiSecurityHardeningTests
{
    private const string AgentConfig = """
    {
      "channels": {
        "telegram": { "enabled": true, "bot_token": "8787000000:AAH-real-token-value", "bot_prefix": "" },
        "matrix": { "access_token": "syt_bW9ucw_real", "homeserver": "http://x" },
        "zalo": { "secret_token": "abcd1234" }
      },
      "mcp": { "clients": {
        "kb": { "env": { "OUTLINE_API_TOKEN": "ol_api_real", "OUTLINE_API_URL": "https://kb" } },
        "gh": { "headers": { "Authorization": "Bearer ghp_real" } },
        "ref": { "env": { "PLANE_API_KEY": "${PLANE_API_KEY}" } }
      } },
      "running": { "max_tokens": 300000, "token_count_estimate_divisor": 4.0, "api_key": "" },
      "providers": [ { "id": "x", "api_key_prefix": "sk-c", "api_key_prefixes": ["sk-a", "sk-b"] } ]
    }
    """;

    [Fact]
    public void RedactJson_MasksChannelTokensMcpSecretsAndKeyPrefixes_KeepsMetadata()
    {
        var red = JsonNode.Parse(SecretRedaction.RedactJson(AgentConfig))!;

        red["channels"]!["telegram"]!["bot_token"]!.GetValue<string>().Should().Be(SecretRedaction.Mask);
        red["channels"]!["matrix"]!["access_token"]!.GetValue<string>().Should().Be(SecretRedaction.Mask);
        red["channels"]!["zalo"]!["secret_token"]!.GetValue<string>().Should().Be(SecretRedaction.Mask);
        red["mcp"]!["clients"]!["kb"]!["env"]!["OUTLINE_API_TOKEN"]!.GetValue<string>().Should().Be(SecretRedaction.Mask);
        red["mcp"]!["clients"]!["gh"]!["headers"]!["Authorization"]!.GetValue<string>().Should().Be(SecretRedaction.Mask);
        red["providers"]![0]!["api_key_prefix"]!.GetValue<string>().Should().Be(SecretRedaction.Mask);
        red["providers"]![0]!["api_key_prefixes"]![1]!.GetValue<string>().Should().Be(SecretRedaction.Mask);

        // Non-secrets stay intact.
        red["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        red["mcp"]!["clients"]!["kb"]!["env"]!["OUTLINE_API_URL"]!.GetValue<string>().Should().Be("https://kb");
        red["mcp"]!["clients"]!["ref"]!["env"]!["PLANE_API_KEY"]!.GetValue<string>().Should().Be("${PLANE_API_KEY}");
        red["running"]!["max_tokens"]!.GetValue<int>().Should().Be(300000);
        red["running"]!["api_key"]!.GetValue<string>().Should().BeEmpty();
        SecretRedaction.RedactJson("not json").Should().Be("not json");
    }

    [Fact]
    public void RestoreMaskedValues_PutsLiveSecretsBack_SoAUiSaveNeverPersistsTheMask()
    {
        var redacted = SecretRedaction.RedactJson(AgentConfig);
        var edited = JsonNode.Parse(redacted)!;
        edited["channels"]!["telegram"]!["bot_prefix"] = "!";              // user edit
        edited["channels"]!["matrix"]!["access_token"] = "syt_new_token";   // user rotated this one

        var restored = JsonNode.Parse(SecretRedaction.RestoreMaskedValues(edited.ToJsonString(), AgentConfig))!;

        restored["channels"]!["telegram"]!["bot_token"]!.GetValue<string>().Should().Be("8787000000:AAH-real-token-value");
        restored["channels"]!["telegram"]!["bot_prefix"]!.GetValue<string>().Should().Be("!");
        restored["channels"]!["matrix"]!["access_token"]!.GetValue<string>().Should().Be("syt_new_token");
        restored["mcp"]!["clients"]!["gh"]!["headers"]!["Authorization"]!.GetValue<string>().Should().Be("Bearer ghp_real");
        restored.ToJsonString().Should().NotContain(SecretRedaction.Mask);

        // Without a live copy the masked field is dropped instead of being written as stars.
        var dropped = JsonNode.Parse(SecretRedaction.RestoreMaskedValues(redacted, null))!;
        dropped["channels"]!["telegram"]!.AsObject().ContainsKey("bot_token").Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/v1/agents/approvals/approve", true)]
    [InlineData("/api/approvals/deny", true)]
    [InlineData("/api/v1/agents/compact-context", true)]
    [InlineData("/api/v1/agents/mons/compact-context", true)]
    [InlineData("/api/v1/agents/mons/active-model", true)]
    [InlineData("/api/v1/agents/iam/revoke", true)]
    [InlineData("/api/v1/libraries/skills/x/toggle", true)]
    [InlineData("/api/v1/mcps/x/toggle", true)]
    [InlineData("/api/v1/tools/x/toggle", true)]
    [InlineData("/api/v1/schedules/x/run", true)]
    [InlineData("/api/v1/media/upload", true)]
    [InlineData("/api/v1/agents/mons/media/send", true)]
    [InlineData("/api/v1/chats/s1/share", true)]
    [InlineData("/api/v1/integrations/composio/connect", true)]
    [InlineData("/api/v1/mcp", true)]
    [InlineData("/api/v1/agents/console/chat", false)]
    [InlineData("/api/console/chat", false)]
    [InlineData("/api/v1/agents/console/chat/stop", false)]
    [InlineData("/api/v1/agents/console/upload", false)]
    [InlineData("/api/v1/console/upload", false)]
    [InlineData("/api/console/upload", false)]
    [InlineData("/api/v1/agents/analytics/turn-telemetry", false)]
    [InlineData("/api/v1/agents/mons/browser/focus", false)]
    [InlineData("/health", false)]
    public void OperatorWriteGuard_ProtectsMutatingRoutes_LeavesChatSurfaceOpen(string path, bool protectedRoute)
    {
        var rel = OperatorWriteGuardMiddleware.Relative(path);
        (rel is not null && OperatorWriteGuardMiddleware.IsProtected(rel)).Should().Be(protectedRoute);
    }

    [Theory]
    [InlineData("agents/mons/active-model", true)]
    [InlineData("agents/mons/compact-context", true)]
    [InlineData("agents/mons/toggle", true)]
    [InlineData("agents/iam/revoke", false)]
    [InlineData("mcps/x/toggle", false)]
    public void OperatorWriteGuard_AllowsAuthenticatedUserOnOwnedAgentWriteRoutes(string rel, bool expected)
    {
        OperatorWriteGuardMiddleware.IsUserOwnedAgentWrite(rel).Should().Be(expected);
    }

    [Theory]
    [InlineData("agents/mons/chats", true)]
    [InlineData("agents/mons/chats/abc", true)]
    [InlineData("agents/chats/abc", true)]
    [InlineData("agents/workspace/tree", true)]
    [InlineData("agents/workspace/file-content", true)]
    [InlineData("agents/mons/workspace/file-content", true)]
    [InlineData("agents/mons/workspace/tree", true)]
    [InlineData("agents/mons/memory", true)]
    [InlineData("agents/sessions/s1/tool-receipts", true)]
    [InlineData("chats/s1", true)]
    [InlineData("shared/share_abc", false)]
    [InlineData("agents", false)]
    [InlineData("agents/mons", false)]
    [InlineData("agents/mons/agent-status", false)]
    [InlineData("agents/mons/skills", false)]
    [InlineData("agents/mons/mcp", false)]
    [InlineData("agents/models", false)]
    [InlineData("agents/mons/workspace/checkpoints/status", false)]
    [InlineData("agents/mons/browser", true)]
    [InlineData("agents/mons/browser/pages", true)]
    [InlineData("agents/mons/browser/image.jpg", true)]
    [InlineData("agents/mons/browser/screenshot", true)]
    [InlineData("agents/mons/browser/stream", true)]
    [InlineData("agents/mons/browser/health", false)]
    public void SensitiveReadGuard_CoversWorkspaceAndChatReads_Only(string rel, bool sensitive)
    {
        SensitiveReadGuardMiddleware.IsSensitive(rel).Should().Be(sensitive);
    }

    [Fact]
    public async Task SensitiveReadGuard_RejectsAnonymous_AllowsOperatorOrAuthenticated()
    {
        var opts = new Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions { OperatorToken = "op-secret", RequireAuthForSensitiveReads = true };
        var monitor = new StaticMonitor(opts);
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };
        var reached = 0;
        var mw = new SensitiveReadGuardMiddleware(_ => { reached++; return Task.CompletedTask; });

        HttpContext Ctx(string path) { var c = new DefaultHttpContext(); c.Request.Method = "GET"; c.Request.Path = path; c.Response.Body = new MemoryStream(); return c; }

        var anon = Ctx("/api/v1/agents/mons/chats");
        await mw.InvokeAsync(anon, monitor, env);
        anon.Response.StatusCode.Should().Be(401);

        // A syntactically present but unvalidated bearer must not bypass the guard.
        var forged = Ctx("/api/v1/agents/mons/chats");
        forged.Request.Headers.Authorization = "Bearer test-member-token";
        await mw.InvokeAsync(forged, monitor, env);
        forged.Response.StatusCode.Should().Be(401);

        var op = Ctx("/api/v1/agents/mons/chats");
        op.Request.Headers["X-Agent360-Operator-Token"] = "op-secret";
        await mw.InvokeAsync(op, monitor, env);

        var user = Ctx("/api/agents/mons/workspace/file-content");
        user.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "u1")], "Bearer"));
        await mw.InvokeAsync(user, monitor, env);

        var pub = Ctx("/api/v1/agents/mons/agent-status");
        await mw.InvokeAsync(pub, monitor, env);

        reached.Should().Be(3);
        opts.RequireAuthForSensitiveReads = false;
        var off = Ctx("/api/v1/agents/mons/chats");
        await mw.InvokeAsync(off, monitor, env);
        reached.Should().Be(4);
    }

    [Fact]
    public void ChatVisibility_SharedMemberSeesOnlyOwnChats_AdminSeesAll()
    {
        const string chats = """
            [{"id":"a","session_id":"s-a","user_id":"skyer:u1"},
             {"id":"b","session_id":"s-b","user_id":"default"},
             {"id":"c","session_id":"s-c","user_id":"skyer:u2"}]
            """;
        var own = System.Text.Json.JsonDocument.Parse(ChatVisibilityGuard.FilterChatsJson(chats, "skyer:u1")).RootElement;
        own.GetArrayLength().Should().Be(1);
        own[0].GetProperty("id").GetString().Should().Be("a");
        ChatVisibilityGuard.FilterChatsJson(chats, null).Should().Be(chats);
        System.Text.Json.JsonDocument.Parse(ChatVisibilityGuard.FilterChatsJson(chats, "")).RootElement.GetArrayLength().Should().Be(0);

        ChatVisibilityGuard.CanAccessChat(chats, "a", "skyer:u1").Should().BeTrue();
        ChatVisibilityGuard.CanAccessChat(chats, "s-a", "skyer:u1").Should().BeTrue();
        ChatVisibilityGuard.CanAccessChat(chats, "b", "skyer:u1").Should().BeFalse();
        ChatVisibilityGuard.CanAccessChat(chats, "s-c", "skyer:u1").Should().BeFalse();
        ChatVisibilityGuard.CanAccessChat(chats, "session-new", "skyer:u1").Should().BeTrue();
        ChatVisibilityGuard.CanAccessChat(chats, "session-new", "").Should().BeFalse();
        ChatVisibilityGuard.CanAccessChat(chats, "c", null).Should().BeTrue();
    }

    private sealed class StaticMonitor(Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions v)
        : Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions>
    {
        public Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions CurrentValue => v;
        public Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions Get(string? name) => v;
        public IDisposable? OnChange(Action<Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions, string?> listener) => null;
    }

    [Theory]
    [InlineData("1my9xeouvb48", "1my9xeouvb48", true)]
    [InlineData("a1; 1my9xeouvb48 ,b2", "1my9xeouvb48", true)]
    [InlineData("1my9xeouvb48", "1MY9XEOUVB48", false)]
    [InlineData("1my9xeouvb48", "other", false)]
    [InlineData("", "1my9xeouvb48", false)]
    [InlineData("1my9xeouvb48", null, false)]
    public void AdminSubjects_MatchExactLogtoSubjects(string configured, string? sub, bool expected)
    {
        new Agent360.API.Features.Agents.SubAgents.Agent360SecurityOptions { AdminSubjects = configured }.IsAdminSubject(sub).Should().Be(expected);
    }

    [Fact]
    public async Task SecurityHeaders_AreSetAndServerHeaderRemoved()
    {
        var ctx = new DefaultHttpContext();
        var feature = new StartingFeature();
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(feature);
        ctx.Response.Headers.Server = "Kestrel";
        var mw = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await mw.InvokeAsync(ctx);
        await feature.FireAsync();

        ctx.Response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        ctx.Response.Headers["X-Frame-Options"].ToString().Should().Be("DENY");
        ctx.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        ctx.Response.Headers.ContainsKey("Server").Should().BeFalse();
    }

    private sealed class StartingFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        private readonly List<(Func<object, Task> cb, object state)> _cbs = [];
        public override void OnStarting(Func<object, Task> callback, object state) => _cbs.Add((callback, state));
        public async Task FireAsync() { foreach (var (cb, st) in _cbs) await cb(st); }
    }
    [Fact]
    public void AgentSwitching_ChatsIsolation_And_RoutingEquivalence()
    {
        // 1. Equivalent slugs resolution for test agents
        var equiv = Agent360.API.Features.Agents.Security.AgentOwnershipGuard.ExpandEquivalentAgentSlugs("agentcore-test-ba");
        equiv.Should().Contain("qwenpaw-test-ba");
        equiv.Should().Contain("default-test");

        // 2. Chat list filtering strictly isolates chats per agent profile
        const string testBaChats = """
            [{"id":"chat-ba-1","session_id":"s-ba-1","user_id":"skyer:u1"},
             {"id":"chat-ba-2","session_id":"s-ba-2","user_id":"skyer:u1"}]
            """;
        const string mkaiChats = """
            [{"id":"chat-mkai-1","session_id":"s-mkai-1","user_id":"default"},
             {"id":"chat-mkai-2","session_id":"s-mkai-2","user_id":"skyer:u2"}]
            """;

        // User 1 sees 2 chats on test-ba
        var filteredBa = Agent360.API.Features.Agents.Security.ChatVisibilityGuard.FilterChatsJson(testBaChats, "skyer:u1");
        System.Text.Json.JsonDocument.Parse(filteredBa).RootElement.GetArrayLength().Should().Be(2);

        // User 1 switched to mkai (where user 1 is assigned as Operator/Owner -> scope is null: sees all)
        var filteredMkaiAssigned = Agent360.API.Features.Agents.Security.ChatVisibilityGuard.FilterChatsJson(mkaiChats, null);
        System.Text.Json.JsonDocument.Parse(filteredMkaiAssigned).RootElement.GetArrayLength().Should().Be(2);

        // User 1 switched to mkai as a non-assigned member (scope = "skyer:u1") -> sees 0 chats
        var filteredMkaiUnassigned = Agent360.API.Features.Agents.Security.ChatVisibilityGuard.FilterChatsJson(mkaiChats, "skyer:u1");
        System.Text.Json.JsonDocument.Parse(filteredMkaiUnassigned).RootElement.GetArrayLength().Should().Be(0);
    }

}
