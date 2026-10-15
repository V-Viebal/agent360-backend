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
}
