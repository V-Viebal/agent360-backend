using System.Text.Json;
using Agent360.API.Features.Agents.Models;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class AgentFeaturesMappingTests
{
    [Fact]
    public void ApprovalListResponse_DeserializesCorrectly()
    {
        const string json = """
        {
          "pending_approvals": [
            {
              "request_id": "req-1234",
              "session_id": "sess-5678",
              "root_session_id": "sess-5678",
              "agent_id": "mons",
              "tool_name": "execute_shell_command",
              "severity": "high",
              "created_at": "2026-10-15T09:30:00Z",
              "reasoning": "Deploying staging container"
            }
          ],
          "count": 1
        }
        """;

        var response = JsonSerializer.Deserialize<ApprovalListResponseDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        Assert.Equal(1, response.Count);
        Assert.Single(response.PendingApprovals);
        Assert.Equal("req-1234", response.PendingApprovals[0].RequestId);
        Assert.Equal("execute_shell_command", response.PendingApprovals[0].ToolName);
    }

    [Fact]
    public void SkillList_DeserializesCorrectly()
    {
        const string json = """
        [
          {
            "name": "browser_cdp",
            "description": "Safely bind and inspect Chromium CDP",
            "source": "custom",
            "enabled": true,
            "channels": ["all"],
            "tags": [],
            "last_updated": "2026-10-13T10:45:51Z"
          }
        ]
        """;

        var skills = JsonSerializer.Deserialize<IReadOnlyList<QwenPawSkillDto>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(skills);
        Assert.Single(skills);
        Assert.Equal("browser_cdp", skills[0].Name);
        Assert.True(skills[0].Enabled);
    }
}
