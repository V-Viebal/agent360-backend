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
    [Fact]
    public void AgentStatsSummary_DeserializesCorrectly()
    {
        const string json = """
        {
          "total_active_sessions": 42,
          "total_messages": 350,
          "total_user_messages": 180,
          "total_assistant_messages": 170,
          "total_prompt_tokens": 1250000,
          "total_completion_tokens": 320000,
          "total_llm_calls": 380,
          "total_tool_calls": 210,
          "start_date": "2026-09-15",
          "end_date": "2026-10-15",
          "by_date": [
            {
              "date": "2026-10-14",
              "chats": 12,
              "active_sessions": 5,
              "user_messages": 30,
              "assistant_messages": 28,
              "total_messages": 58,
              "prompt_tokens": 140000,
              "completion_tokens": 35000,
              "llm_calls": 40,
              "tool_calls": 25
            }
          ],
          "channel_stats": [
            {
              "channel": "telegram",
              "session_count": 24,
              "user_messages": 110,
              "assistant_messages": 105,
              "total_messages": 215
            }
          ]
        }
        """;

        var stats = JsonSerializer.Deserialize<AgentStatsSummaryDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(stats);
        Assert.Equal(42, stats.TotalActiveSessions);
        Assert.Equal(350, stats.TotalMessages);
        Assert.Equal(1250000, stats.TotalPromptTokens);
        Assert.Equal(210, stats.TotalToolCalls);
        Assert.Single(stats.ByDate);
        Assert.Equal("2026-10-14", stats.ByDate[0].Date);
        Assert.Single(stats.ChannelStats);
        Assert.Equal("telegram", stats.ChannelStats[0].Channel);
        Assert.Equal(24, stats.ChannelStats[0].SessionCount);
    }

    [Fact]
    public void LlmToolTrend_DeserializesCorrectly()
    {
        const string json = """
        [
          {
            "date": "2026-10-14",
            "agent_llm_calls": 48,
            "tool_calls": 32
          },
          {
            "date": "2026-10-15",
            "agent_llm_calls": 62,
            "tool_calls": 41
          }
        ]
        """;

        var trend = JsonSerializer.Deserialize<IReadOnlyList<LlmToolDailyDto>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(trend);
        Assert.Equal(2, trend.Count);
        Assert.Equal("2026-10-14", trend[0].Date);
        Assert.Equal(48, trend[0].AgentLlmCalls);
        Assert.Equal(32, trend[0].ToolCalls);
    }
}
