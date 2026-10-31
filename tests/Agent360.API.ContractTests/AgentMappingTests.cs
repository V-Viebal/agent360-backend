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


    [Fact]
    public async Task UpdateAgentProfileAndChat_AlwaysEnforcesApprovalLevelOff()
    {
        string? capturedPutBody = null;
        string? capturedChatBody = null;

        var routing = new Agent360.API.Features.Agents.Services.FleetInstanceRoutingService(
            new HttpClient(new StubMessageHandler(req =>
            {
                var path = req.RequestUri?.AbsolutePath ?? "";
                if (req.Method == System.Net.Http.HttpMethod.Get && path.EndsWith("/api/agents/default"))
                {
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent("{\"id\":\"default\",\"name\":\"Test Agent\",\"approval_level\":\"AUTO\"}")
                    };
                }
                if (req.Method == System.Net.Http.HttpMethod.Put && path.EndsWith("/api/agents/default"))
                {
                    capturedPutBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent(capturedPutBody ?? "{}")
                    };
                }
                if (req.Method == System.Net.Http.HttpMethod.Post && path.EndsWith("/console/chat"))
                {
                    capturedChatBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent("data: [DONE]\n\n")
                    };
                }
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("{\"access_token\":\"tok\"}")
                };
            })),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Services.FleetInstanceRoutingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()));

        var client = new Agent360.API.Features.Agents.Services.QwenPawClient(
            new HttpClient(),
            routing,
            Microsoft.Extensions.Options.Options.Create(new Agent360.API.Options.QwenPawOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent360.API.Features.Agents.Services.QwenPawClient>.Instance);

        // Even if caller passes approval_level=AUTO, UpdateAgentProfileRawAsync must force approval_level=OFF
        var patch = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "Skyer Updated",
            ["approval_level"] = "AUTO"
        };
        var updatedRaw = await client.UpdateAgentProfileRawAsync("qwenpaw-test-ba", patch, TestContext.Current.CancellationToken);
        Assert.NotNull(updatedRaw);
        Assert.Contains("\"approval_level\":\"OFF\"", capturedPutBody);

        // StreamConsoleChatAsync must also inject request_context.approval_level = OFF
        using var chatResp = await client.StreamConsoleChatAsync(
            new ConsoleChatSendRequestDto("session-1", "hello", "qwenpaw-test-ba", null, null),
            TestContext.Current.CancellationToken);
        Assert.NotNull(capturedChatBody);
        Assert.Contains("\"approval_level\":\"OFF\"", capturedChatBody);

        // QwenPawA2AClient.BuildPayload must also inject approval_level = OFF
        var a2aJson = System.Text.Json.JsonSerializer.Serialize(
            Agent360.API.Features.Agents.SubAgents.QwenPawA2AClient.BuildPayload("mons", "sess-a2a", "task", null));
        Assert.Contains("\"approval_level\":\"OFF\"", a2aJson);
    }


    [Fact]
    public async Task AgentProfileStore_ResolvesAliasAndSyncsCustomSlugAliases()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "a360-profile-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new Agent360.API.Features.Agents.Endpoints.AgentProfileStore(
                Microsoft.Extensions.Options.Options.Create(new Agent360.API.Features.Agents.SubAgents.SubAgentOptions { StoragePath = tempDir }),
                new TestEnv(tempDir));

            var payload = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "default-test",
                ["avatar"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "blob" }
            };
            var saved = await store.SetAsync("qwenpaw-test-ba", payload, TestContext.Current.CancellationToken);
            Assert.Equal("default-test", saved["name"]?.ToString());
            Assert.Equal("OFF", saved["approval_level"]?.ToString());

            // Alias must resolve in store and in FleetRoutingState.CustomSlugAliases
            Assert.Equal("qwenpaw-test-ba", store.ResolveCanonicalSlugByAlias("default-test"));
            Assert.Equal("qwenpaw-test-ba", Agent360.API.Features.Agents.Services.FleetInstanceRoutingService.NormalizeAgentId("default-test"));
            Assert.NotNull(store.Get("default-test"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }


    [Fact]
    public void SessionRoomStore_EnrichChatHistoryJson_StripsInternalRuntimeNoticesAndHeadlines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "a360-room-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new Agent360.API.Features.Agents.SubAgents.SessionRoomStore(new TestEnv(tempDir));

            const string rawHistory = """
            {
              "messages": [
                {
                  "id": "m-user-1",
                  "role": "user",
                  "type": "message",
                  "content": [{ "type": "text", "text": "tóm tắt đi" }],
                  "metadata": { "original_name": "user" }
                },
                {
                  "id": "m-sys-notice",
                  "role": "assistant",
                  "type": "message",
                  "content": [{ "type": "text", "text": "<system-notification>\nThe tools available to you changed since earlier in this conversation.\n</system-notification>" }],
                  "metadata": { "original_name": "system" }
                },
                {
                  "id": "m-asst-1",
                  "role": "assistant",
                  "type": "message",
                  "content": [{ "type": "text", "text": "<system-notification>\nBackground tool call finished.\n</system-notification>\n\nEm tóm tắt cuốn theo 13 chương, bằng lời của em, không dịch nguyên văn.\n\n⟦ Dzogchen Teachings summary | completed ⟧" }],
                  "metadata": { "original_name": "Default" }
                }
              ],
              "status": "idle"
            }
            """;

            var enriched = store.EnrichChatHistoryJson(rawHistory, "qwenpaw-test-ba", "chat-test-123");
            using var parsedDoc = System.Text.Json.JsonDocument.Parse(enriched);
            var msgs = parsedDoc.RootElement.GetProperty("messages");
            Assert.Equal(2, msgs.GetArrayLength());
            Assert.Equal("m-user-1", msgs[0].GetProperty("id").GetString());
            Assert.Equal("m-asst-1", msgs[1].GetProperty("id").GetString());
            var asstText = msgs[1].GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            Assert.DoesNotContain("<system-notification>", asstText);
            Assert.DoesNotContain("⟦", asstText);
            Assert.Equal("Em tóm tắt cuốn theo 13 chương, bằng lời của em, không dịch nguyên văn.", asstText);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private sealed class StubMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
