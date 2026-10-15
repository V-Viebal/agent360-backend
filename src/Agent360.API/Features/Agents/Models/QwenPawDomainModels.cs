using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

// --- Skills ---
public sealed record QwenPawSkillDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("channels")] IReadOnlyList<string>? Channels,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("last_updated")] string? LastUpdated);

// --- Approvals ---
public sealed record ApprovalActionRequestDto(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("scope")] string? Scope);

public sealed record ApprovalActionResponseDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("request_id")] string RequestId);

public sealed record ApprovalItemDto(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("root_session_id")] string? RootSessionId,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("reasoning")] string? Reasoning);

public sealed record ApprovalListResponseDto(
    [property: JsonPropertyName("pending_approvals")] IReadOnlyList<ApprovalItemDto> PendingApprovals,
    [property: JsonPropertyName("count")] int Count);

// --- Chat history & messages ---
public sealed record ChatHistoryMessageDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("created_at")] string? CreatedAt);

public sealed record ChatHistoryResponseDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatHistoryMessageDto>? Messages);

// --- Console Chat Send/Stop ---
public sealed record ConsoleChatSendRequestDto(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("model_slot_override")] string? ModelSlotOverride);

public sealed record ConsoleChatStopRequestDto(
    [property: JsonPropertyName("session_id")] string SessionId);

// --- Workspace File Browser ---
public sealed record WorkspaceTreeNodeDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("children")] IReadOnlyList<WorkspaceTreeNodeDto>? Children);

public sealed record WorkspaceFileContentDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string? Content);

// --- Active Model Override ---
public sealed record SetActiveModelRequestDto(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("model")] string Model);

public sealed record SetActiveModelResponseDto(
    [property: JsonPropertyName("active_llm")] QwenPawActiveModel? ActiveLlm,
    [property: JsonPropertyName("effective_max_input_length")] long? EffectiveMaxInputLength);
