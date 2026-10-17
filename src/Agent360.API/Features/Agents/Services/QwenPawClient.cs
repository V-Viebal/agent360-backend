using System.Net.Http.Json;
using System.Text.Json;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Options;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Services;

public sealed class QwenPawClient : IQwenPawClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QwenPawClient> _logger;
    private readonly QwenPawOptions _options;

    public QwenPawClient(
        HttpClient httpClient,
        IOptions<QwenPawOptions> options,
        ILogger<QwenPawClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<IReadOnlyList<AgentSummaryResponse>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<QwenPawAgentListResponse>(
                "api/agents",
                cancellationToken);

            if (response?.Agents is null or { Count: 0 })
            {
                return Array.Empty<AgentSummaryResponse>();
            }

            var results = new List<AgentSummaryResponse>(response.Agents.Count);
            foreach (var a in response.Agents)
            {
                // Only load enabled agents as requested
                if (!a.Enabled)
                {
                    continue;
                }
                var roleTag = DetermineRoleTag(a.Id, a.Name, a.Description);
                var activeModelStr = a.ActiveModel?.Model ?? a.BackendModel ?? "grok-4.7";
                if (!string.IsNullOrWhiteSpace(a.ActiveModel?.ProviderId))
                {
                    activeModelStr = $"{a.ActiveModel.ProviderId}/{activeModelStr}";
                }

                var status = a.StartupStatus switch
                {
                    "running" => "active",
                    "pending" => "evaluating",
                    "disabled" => "idle",
                    _ => a.Enabled ? "active" : "idle"
                };

                var tools = BuildDefaultTools(a);

                results.Add(new AgentSummaryResponse(
                    Id: $"bot:agent360:{a.Id}",
                    Name: string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name,
                    Slug: a.Id,
                    RoleTag: roleTag,
                    Node: "3rd (ai.hmz.one)",
                    Status: status,
                    Model: activeModelStr,
                    RuntimeVersion: "qwenpaw-adapter v2.2.1",
                    Uptime: "Online",
                    Description: a.Description,
                    Enabled: a.Enabled,
                    Pinned: a.Pinned,
                    AvailableInChat: a.AvailableInChat,
                    Tools: tools));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load agents from QwenPaw API at {BaseUrl}", _options.BaseUrl);
            throw;
        }
    }

    
    public async Task<string> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("api/models", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> GetAgentProfileRawAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/agents/{agentId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<SetActiveModelResponseDto?> SetAgentActiveModelAsync(string agentId, string providerId, string model, CancellationToken cancellationToken = default)
    {
        var payload = new SetActiveModelRequestDto(
            Scope: "agent",
            AgentId: agentId,
            ProviderId: providerId,
            Model: model);

        var response = await _httpClient.PutAsJsonAsync("api/models/active", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SetActiveModelResponseDto>(cancellationToken: cancellationToken);
    }

    
    public async Task<string> GetAgentChatsRawAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/agents/{agentId}/chats", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentSessionResponse>> GetChatsAsync(CancellationToken cancellationToken = default)
    {
        var chats = await _httpClient.GetFromJsonAsync<IReadOnlyList<QwenPawChatDto>>(
            "api/chats",
            cancellationToken);

        if (chats is null)
        {
            return Array.Empty<AgentSessionResponse>();
        }

        return chats.Select(c => new AgentSessionResponse(
            Id: !string.IsNullOrWhiteSpace(c.Id) ? c.Id : (c.SessionId ?? ""),
            Title: string.IsNullOrWhiteSpace(c.Name) ? "Session" : c.Name,
            UpdatedAt: c.UpdatedAt,
            MessageCount: 1,
            LastMessageSnippet: c.Name,
            Unread: false
        )).ToList();
    }

    public async Task<string> GetChatHistoryRawAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/chats/{chatId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<ChatHistoryResponseDto?> GetChatHistoryAsync(string chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ChatHistoryResponseDto>(
                $"api/chats/{chatId}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deserializing ChatHistoryResponseDto directly failed for chat {ChatId}. Falling back to raw JSON parsing.", chatId);
            return null;
        }
    }

    public async Task<IReadOnlyList<QwenPawSkillDto>> GetSkillsAsync(CancellationToken cancellationToken = default)
    {
        var skills = await _httpClient.GetFromJsonAsync<IReadOnlyList<QwenPawSkillDto>>(
            "api/skills",
            cancellationToken);

        return skills ?? Array.Empty<QwenPawSkillDto>();
    }

    public async Task<ApprovalListResponseDto?> GetApprovalsAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(sessionId)
            ? "api/approval/list"
            : $"api/approval/list?session_id={Uri.EscapeDataString(sessionId)}";

        return await _httpClient.GetFromJsonAsync<ApprovalListResponseDto>(url, cancellationToken);
    }

    public async Task<ApprovalActionResponseDto?> ApproveToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/approval/approve", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApprovalActionResponseDto>(cancellationToken: cancellationToken);
    }

    public async Task<ApprovalActionResponseDto?> DenyToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/approval/deny", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApprovalActionResponseDto>(cancellationToken: cancellationToken);
    }

    public async Task<HttpResponseMessage> StreamConsoleChatAsync(ConsoleChatSendRequestDto request, CancellationToken cancellationToken = default)
    {
        var agentCleanId = (request.AgentId ?? "mons").Replace("bot:agent360:", "").ToLowerInvariant();
        var textContent = request.Message;
        if (string.IsNullOrWhiteSpace(textContent) && request.Input is { Count: > 0 })
        {
            textContent = request.Input[0].Content?.FirstOrDefault()?.Text ?? "";
        }

        var payload = new
        {
            user_id = "default",
            session_id = !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : $"session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            channel = "console",
            input = new[]
            {
                new
                {
                    role = "user",
                    type = "message",
                    content = new[]
                    {
                        new { type = "text", text = textContent ?? "" }
                    }
                }
            }
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/console/chat")
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.TryAddWithoutValidation("X-Agent-Id", agentCleanId);

        // Use a dedicated long-lived timeout CTS for live LLM streaming turns
        // Stream response requires infinite client timeout or long token timeout
        return await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public async Task<bool> StopChatAsync(ConsoleChatStopRequestDto request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/console/chat/stop", request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<WorkspaceTreeNodeDto>?> GetWorkspaceTreeAsync(CancellationToken cancellationToken = default)
    {
        var rawResponse = await _httpClient.GetFromJsonAsync<QwenPawWorkspaceTreeResponse>(
            "api/workspace/tree",
            cancellationToken);

        if (rawResponse?.Entries is null) return [];

        return rawResponse.Entries.Select(e => new WorkspaceTreeNodeDto(
            e.Name,
            e.Path,
            e.Kind,
            e.Size,
            null
        )).ToList();
    }

    public async Task<string?> GetFileContentAsync(string path, CancellationToken cancellationToken = default)
    {
        var url = $"api/workspace/file-content?path={Uri.EscapeDataString(path)}";
        var result = await _httpClient.GetFromJsonAsync<WorkspaceFileContentDto>(url, cancellationToken);
        return result?.Content;
    }

    private static string DetermineRoleTag(string id, string name, string? desc)
    {
        var text = $"{id} {name} {desc}".ToLowerInvariant();
        if (text.Contains("qa") || text.Contains("quality") || text.Contains("test"))
        {
            return "Quality";
        }
        if (text.Contains("ops") || text.Contains("sre") || text.Contains("infra") || text.Contains("sentinel"))
        {
            return "Operations";
        }
        if (text.Contains("research") || text.Contains("kb") || text.Contains("knowledge") || text.Contains("po") || text.Contains("product"))
        {
            return "Research";
        }
        if (text.Contains("security") || text.Contains("iam") || text.Contains("guard"))
        {
            return "Security";
        }
        return "Engineering";
    }

    private static IReadOnlyList<AgentToolSummary> BuildDefaultTools(QwenPawAgentDto agent)
    {
        var list = new List<AgentToolSummary>
        {
            new("Terminal / Shell", "Native Process", "Execute bounded workspace and project shell commands"),
            new("Agent Kernel ACP", "Protocol Adapter", "Multi-bot delegation and event routing"),
            new("Outline KB MCP", "Knowledge Store", "Single Source of Truth context synchronization")
        };

        if (agent.BackendCapabilities?.ContainsKey("workspace_ui") == true)
        {
            list.Add(new("Workspace UI", "Web Capability", "Interactive browser and file tree access"));
        }

        return list;
    }
    public async Task<AgentStatsSummaryDto?> GetAgentStatsAsync(string? startDate = null, string? endDate = null, CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(startDate)) queryParams.Add($"start_date={Uri.EscapeDataString(startDate)}");
        if (!string.IsNullOrWhiteSpace(endDate)) queryParams.Add($"end_date={Uri.EscapeDataString(endDate)}");
        
        var url = "api/agent-stats" + (queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "");
        return await _httpClient.GetFromJsonAsync<AgentStatsSummaryDto>(url, cancellationToken);
    }

    public async Task<IReadOnlyList<LlmToolDailyDto>?> GetGlobalLlmToolTrendAsync(string? startDate = null, string? endDate = null, CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(startDate)) queryParams.Add($"start_date={Uri.EscapeDataString(startDate)}");
        if (!string.IsNullOrWhiteSpace(endDate)) queryParams.Add($"end_date={Uri.EscapeDataString(endDate)}");

        var url = "api/agent-stats/llm-tool-trend" + (queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "");
        return await _httpClient.GetFromJsonAsync<List<LlmToolDailyDto>>(url, cancellationToken);
    }

    public async Task<TokenUsageResponseDto?> GetTokenUsageAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<TokenUsageResponseDto>("api/token-usage", cancellationToken);
    }
}
