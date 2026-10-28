using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using Agent360.API.Features.Agents.Models;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class AgentMappingTests
{
    [Fact]
    public void QwenPawAgentListResponse_DeserializesCorrectly()
    {
        const string json = """
        {
          "agents": [
            {
              "id": "mons",
              "name": "Mons",
              "description": "Core Development Agent",
              "workspace_dir": "/app/working/workspaces/mons",
              "enabled": true,
              "pinned": false,
              "startup_status": "running",
              "backend": "qwenpaw",
              "backend_capabilities": { "workspace_ui": true },
              "backend_model": null,
              "backend_reasoning_effort": null,
              "active_model": {
                "provider_id": "llm360coding",
                "model": "grok-4.7"
              },
              "managed_by_app": null,
              "available_in_chat": true
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<QwenPawAgentListResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        Assert.Single(response.Agents);

        var agent = response.Agents[0];
        Assert.Equal("mons", agent.Id);
        Assert.Equal("Mons", agent.Name);
        Assert.True(agent.Enabled);
        Assert.Equal("running", agent.StartupStatus);
        Assert.Equal("grok-4.7", agent.ActiveModel?.Model);
        Assert.Equal("llm360coding", agent.ActiveModel?.ProviderId);
    }

    [Fact]
    public async Task PinAgentAsync_HonorsUnpinEvenWhenRuntimeRejectsUnpinningDefaultProfile()
    {
        var routing = new Agent360.API.Features.Agents.Services.FleetInstanceRoutingService(
            new HttpClient(new StubMessageHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new System.Net.Http.StringContent("{\"detail\":\"Cannot unpin the default agent\"}")
            })),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Services.FleetInstanceRoutingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()));

        var client = new Agent360.API.Features.Agents.Services.QwenPawClient(
            new HttpClient(),
            routing,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Services.QwenPawClient>.Instance);

        var unpinRes = await client.PinAgentAsync("gm-boc", false, TestContext.Current.CancellationToken);
        Assert.NotNull(unpinRes);
        Assert.True(unpinRes!.Success);
        Assert.Equal("gm-boc", unpinRes.AgentId);
        Assert.False(unpinRes.Pinned);

        var repinRes = await client.PinAgentAsync("gm-boc", true, TestContext.Current.CancellationToken);
        Assert.NotNull(repinRes);
        Assert.True(repinRes!.Pinned);
    }


    [Fact]
    public async Task PinAgentAsync_IsolatesPinStatePerUserScope()
    {
        var routing = new Agent360.API.Features.Agents.Services.FleetInstanceRoutingService(
            new HttpClient(new StubMessageHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{\"success\":true,\"agent_id\":\"mons\",\"pinned\":true}")
            })),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Services.FleetInstanceRoutingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()));

        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor();
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        cache.Set("agent360:agents:v2", (IReadOnlyList<AgentSummaryResponse>)new[]
        {
            new AgentSummaryResponse("bot:agent360:mons", "Mons", "mons", "Core", "Node 3rd", "active", "grok-4.7", "v2.2", "Online", "Desc", true, true, true, Array.Empty<AgentToolSummary>())
        });

        var client = new Agent360.API.Features.Agents.Services.QwenPawClient(
            new HttpClient(),
            routing,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Services.QwenPawClient>.Instance,
            cache,
            accessor);

        // User A unpins mons
        var ctxA = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctxA.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("sub", "user-alpha-001")
        }, "TestAuth"));
        accessor.HttpContext = ctxA;

        var scopeA = client.ResolveCallerUserScopeKey(out _, out var subA);
        Assert.Equal("sub:user-alpha-001", scopeA);
        Assert.Equal("user-alpha-001", subA);

        await client.PinAgentAsync("mons", false, TestContext.Current.CancellationToken);

        // Restore cache entry that PinAgentAsync invalidated to verify isolated read
        cache.Set("agent360:agents:v2", (IReadOnlyList<AgentSummaryResponse>)new[]
        {
            new AgentSummaryResponse("bot:agent360:mons", "Mons", "mons", "Core", "Node 3rd", "active", "grok-4.7", "v2.2", "Online", "Desc", true, true, true, Array.Empty<AgentToolSummary>())
        });

        // User B pins mons
        var ctxB = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctxB.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("sub", "user-beta-002")
        }, "TestAuth"));
        accessor.HttpContext = ctxB;

        await client.PinAgentAsync("mons", true, TestContext.Current.CancellationToken);
        cache.Set("agent360:agents:v2", (IReadOnlyList<AgentSummaryResponse>)new[]
        {
            new AgentSummaryResponse("bot:agent360:mons", "Mons", "mons", "Core", "Node 3rd", "active", "grok-4.7", "v2.2", "Online", "Desc", true, true, true, Array.Empty<AgentToolSummary>())
        });

        // Verify User A still sees mons as unpinned (false)
        accessor.HttpContext = ctxA;
        var listA = await client.GetAgentsAsync(TestContext.Current.CancellationToken);
        Assert.False(listA[0].Pinned);

        // Verify User B sees mons as pinned (true)
        accessor.HttpContext = ctxB;
        var listB = await client.GetAgentsAsync(TestContext.Current.CancellationToken);
        Assert.True(listB[0].Pinned);
    }

    private sealed class StubMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
