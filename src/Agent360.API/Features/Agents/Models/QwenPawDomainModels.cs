using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

// --- Skills ---
public sealed record QwenPawSkillDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("emoji")] string? Emoji,
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
    [property: JsonPropertyName("created_at")] double? CreatedAt,
    [property: JsonPropertyName("reasoning")] string? Reasoning,
    [property: JsonPropertyName("timeout_seconds")] double? TimeoutSeconds = null,
    [property: JsonPropertyName("result_summary")] string? ResultSummary = null);

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
public sealed record ConsoleChatContentItem(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text);

public sealed record ConsoleChatInputItem(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("content")] IReadOnlyList<ConsoleChatContentItem>? Content);

public sealed record ConsoleChatSendRequestDto(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("model_slot_override")] string? ModelSlotOverride,
    [property: JsonPropertyName("input")] IReadOnlyList<ConsoleChatInputItem>? Input);

public sealed record ConsoleChatStopRequestDto(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("agent_id")] string? AgentId = null);

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

// --- Token Usage Analytics ---
public sealed record TokenUsageModelDetailDto(
    [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
    [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
    [property: JsonPropertyName("cache_read_tokens")] long CacheReadTokens,
    [property: JsonPropertyName("call_count")] long CallCount,
    [property: JsonPropertyName("provider_id")] string? ProviderId,
    [property: JsonPropertyName("model")] string? Model);

public sealed record TokenUsageResponseDto(
    [property: JsonPropertyName("total_prompt_tokens")] long TotalPromptTokens,
    [property: JsonPropertyName("total_completion_tokens")] long TotalCompletionTokens,
    [property: JsonPropertyName("total_cache_read_tokens")] long TotalCacheReadTokens,
    [property: JsonPropertyName("cache_hit_rate")] double CacheHitRate,
    [property: JsonPropertyName("total_calls")] long TotalCalls,
    [property: JsonPropertyName("by_model")] Dictionary<string, TokenUsageModelDetailDto>? ByModel);


// --- Agent Statistics Models (from QwenPaw agent-stats API) ---

public sealed record ChannelStatsDto(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("session_count")] int SessionCount,
    [property: JsonPropertyName("user_messages")] int UserMessages,
    [property: JsonPropertyName("assistant_messages")] int AssistantMessages,
    [property: JsonPropertyName("total_messages")] int TotalMessages);

public sealed record DailyStatsDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("chats")] int Chats,
    [property: JsonPropertyName("active_sessions")] int ActiveSessions,
    [property: JsonPropertyName("user_messages")] int UserMessages,
    [property: JsonPropertyName("assistant_messages")] int AssistantMessages,
    [property: JsonPropertyName("total_messages")] int TotalMessages,
    [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
    [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
    [property: JsonPropertyName("llm_calls")] int LlmCalls,
    [property: JsonPropertyName("tool_calls")] int ToolCalls,
    [property: JsonPropertyName("agent_prompt_tokens")] long AgentPromptTokens = 0,
    [property: JsonPropertyName("agent_completion_tokens")] long AgentCompletionTokens = 0,
    [property: JsonPropertyName("agent_llm_calls")] int AgentLlmCalls = 0,
    [property: JsonPropertyName("agent_cache_read_tokens")] long AgentCacheReadTokens = 0);

public sealed record AgentStatsSummaryDto(
    [property: JsonPropertyName("total_active_sessions")] int TotalActiveSessions,
    [property: JsonPropertyName("total_messages")] int TotalMessages,
    [property: JsonPropertyName("total_user_messages")] int TotalUserMessages,
    [property: JsonPropertyName("total_assistant_messages")] int TotalAssistantMessages,
    [property: JsonPropertyName("total_prompt_tokens")] long TotalPromptTokens,
    [property: JsonPropertyName("total_completion_tokens")] long TotalCompletionTokens,
    [property: JsonPropertyName("total_llm_calls")] int TotalLlmCalls,
    [property: JsonPropertyName("total_tool_calls")] int TotalToolCalls,
    [property: JsonPropertyName("by_date")] List<DailyStatsDto> ByDate,
    [property: JsonPropertyName("channel_stats")] List<ChannelStatsDto> ChannelStats,
    [property: JsonPropertyName("start_date")] string StartDate,
    [property: JsonPropertyName("end_date")] string EndDate,
    [property: JsonPropertyName("agent_prompt_tokens")] long AgentPromptTokens = 0,
    [property: JsonPropertyName("agent_completion_tokens")] long AgentCompletionTokens = 0,
    [property: JsonPropertyName("agent_llm_calls")] int AgentLlmCalls = 0,
    [property: JsonPropertyName("agent_cache_read_tokens")] long AgentCacheReadTokens = 0,
    [property: JsonPropertyName("agent_cache_eligible_input_tokens")] long AgentCacheEligibleInputTokens = 0,
    [property: JsonPropertyName("agent_cache_hit_rate")] double? AgentCacheHitRate = null);

public sealed record LlmToolDailyDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("agent_llm_calls")] int AgentLlmCalls = 0,
    [property: JsonPropertyName("tool_calls")] int ToolCalls = 0);
