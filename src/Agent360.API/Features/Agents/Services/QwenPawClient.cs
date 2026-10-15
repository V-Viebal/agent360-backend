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
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
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
            Id: c.SessionId ?? c.Id,
            Title: string.IsNullOrWhiteSpace(c.Name) ? "Session" : c.Name,
            UpdatedAt: c.UpdatedAt,
            MessageCount: 1,
            LastMessageSnippet: c.Name,
            Unread: false
        )).ToList();
    }

    public async Task<ChatHistoryResponseDto?> GetChatHistoryAsync(string chatId, CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<ChatHistoryResponseDto>(
            $"api/chats/{chatId}",
            cancellationToken);
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
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/console/chat")
        {
            Content = JsonContent.Create(request)
        };
        return await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public async Task<bool> StopChatAsync(ConsoleChatStopRequestDto request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/console/chat/stop", request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<WorkspaceTreeNodeDto>?> GetWorkspaceTreeAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<IReadOnlyList<WorkspaceTreeNodeDto>>(
            "api/workspace/tree",
            cancellationToken);
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
}
