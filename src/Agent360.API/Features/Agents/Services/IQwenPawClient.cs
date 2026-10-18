using Agent360.API.Features.Agents.Models;

namespace Agent360.API.Features.Agents.Services;

public interface IQwenPawClient
{
    // Agents & Profiles
    Task<IReadOnlyList<AgentSummaryResponse>> GetAgentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentSummaryResponse>> RefreshAgentsAsync(CancellationToken cancellationToken = default);
    Task<string> GetAgentProfileRawAsync(string agentId, CancellationToken cancellationToken = default);
    Task<SetActiveModelResponseDto?> SetAgentActiveModelAsync(string agentId, string providerId, string model, CancellationToken cancellationToken = default);
    Task<string> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<string> GetModelsCompactAsync(CancellationToken cancellationToken = default);

    // Chats & History
    Task<IReadOnlyList<AgentSessionResponse>> GetChatsAsync(CancellationToken cancellationToken = default);
    Task<string> GetAgentChatsRawAsync(string agentId, CancellationToken cancellationToken = default);
    Task<ChatHistoryResponseDto?> GetChatHistoryAsync(string chatId, CancellationToken cancellationToken = default);
    Task<string> GetChatHistoryRawAsync(string chatId, string? agentId = null, CancellationToken cancellationToken = default);

    // Skills
    Task<IReadOnlyList<QwenPawSkillDto>> GetSkillsAsync(string? agentId = null, CancellationToken cancellationToken = default);

    // Approvals
    Task<ApprovalListResponseDto?> GetApprovalsAsync(string? sessionId = null, CancellationToken cancellationToken = default);
    Task<ApprovalActionResponseDto?> ApproveToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default);
    Task<ApprovalActionResponseDto?> DenyToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default);

    // Console Chat & Stop
    Task<HttpResponseMessage> StreamConsoleChatAsync(ConsoleChatSendRequestDto request, CancellationToken cancellationToken = default);
    Task<bool> StopChatAsync(ConsoleChatStopRequestDto request, CancellationToken cancellationToken = default);

    // Workspace & Files
    Task<QwenPawWorkspaceTreeResponse?> GetWorkspaceTreeAsync(string? agentId = null, CancellationToken cancellationToken = default);
    Task<string?> GetFileContentAsync(string path, string? agentId = null, CancellationToken cancellationToken = default);

    // Context Management & Compaction (Native QwenPaw Checkpoint GC & /compact)
    Task<CompactContextResponseDto> CompactContextAsync(CompactContextRequestDto request, CancellationToken cancellationToken = default);

    // Tool Call Receipts & Audit Trail (Native QwenPaw /tool-calls API)
    Task<IReadOnlyList<ToolCallReceiptContractDto>> GetToolCallReceiptsAsync(string sessionId, string? agentId = null, CancellationToken cancellationToken = default);

    // Agent Statistics & Load Metrics (from QwenPaw agent-stats API)
    Task<AgentStatsSummaryDto?> GetAgentStatsAsync(string? startDate = null, string? endDate = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LlmToolDailyDto>?> GetGlobalLlmToolTrendAsync(string? startDate = null, string? endDate = null, CancellationToken cancellationToken = default);

    // Token Usage Analytics
    Task<TokenUsageResponseDto?> GetTokenUsageAsync(CancellationToken cancellationToken = default);
}
