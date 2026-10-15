using Agent360.API.Features.Agents.Models;

namespace Agent360.API.Features.Agents.Services;

public interface IQwenPawClient
{
    // Agents & Profiles
    Task<IReadOnlyList<AgentSummaryResponse>> GetAgentsAsync(CancellationToken cancellationToken = default);
    Task<string> GetAgentProfileRawAsync(string agentId, CancellationToken cancellationToken = default);
    Task<SetActiveModelResponseDto?> SetAgentActiveModelAsync(string agentId, string providerId, string model, CancellationToken cancellationToken = default);

    // Chats & History
    Task<IReadOnlyList<AgentSessionResponse>> GetChatsAsync(CancellationToken cancellationToken = default);
    Task<ChatHistoryResponseDto?> GetChatHistoryAsync(string chatId, CancellationToken cancellationToken = default);

    // Skills
    Task<IReadOnlyList<QwenPawSkillDto>> GetSkillsAsync(CancellationToken cancellationToken = default);

    // Approvals
    Task<ApprovalListResponseDto?> GetApprovalsAsync(string? sessionId = null, CancellationToken cancellationToken = default);
    Task<ApprovalActionResponseDto?> ApproveToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default);
    Task<ApprovalActionResponseDto?> DenyToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default);

    // Console Chat & Stop
    Task<HttpResponseMessage> StreamConsoleChatAsync(ConsoleChatSendRequestDto request, CancellationToken cancellationToken = default);
    Task<bool> StopChatAsync(ConsoleChatStopRequestDto request, CancellationToken cancellationToken = default);

    // Workspace & Files
    Task<IReadOnlyList<WorkspaceTreeNodeDto>?> GetWorkspaceTreeAsync(CancellationToken cancellationToken = default);
    Task<string?> GetFileContentAsync(string path, CancellationToken cancellationToken = default);
}
