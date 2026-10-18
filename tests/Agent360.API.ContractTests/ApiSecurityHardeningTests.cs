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
    [InlineData("/api/v1/agents/analytics/turn-telemetry", false)]
    [InlineData("/api/v1/agents/mons/browser/focus", false)]
    [InlineData("/health", false)]
    public void OperatorWriteGuard_ProtectsMutatingRoutes_LeavesChatSurfaceOpen(string path, bool protectedRoute)
    {
        var rel = OperatorWriteGuardMiddleware.Relative(path);
        (rel is not null && OperatorWriteGuardMiddleware.IsProtected(rel)).Should().Be(protectedRoute);
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
}
