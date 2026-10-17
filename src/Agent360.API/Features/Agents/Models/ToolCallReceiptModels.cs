using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record ToolCallInfoItem(
    [property: JsonPropertyName("tool_call_id")] string ToolCallId,
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("started_at")] double StartedAt,
    [property: JsonPropertyName("elapsed")] double Elapsed,
    [property: JsonPropertyName("offload_remaining")] double? OffloadRemaining,
    [property: JsonPropertyName("kill_remaining")] double? KillRemaining,
    [property: JsonPropertyName("extra")] Dictionary<string, object>? Extra,
    [property: JsonPropertyName("end_state")] string? EndState,
    [property: JsonPropertyName("force_cancelled")] bool ForceCancelled,
    [property: JsonPropertyName("max_internal_timeout_secs")] double? MaxInternalTimeoutSecs,
    [property: JsonPropertyName("offload_reason")] string? OffloadReason);

public sealed record ToolCallReceiptContractDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("tool_call_id")] string ToolCallId,
    [property: JsonPropertyName("bot_id")] string BotId,
    [property: JsonPropertyName("bot_binding_id")] string? BotBindingId,
    [property: JsonPropertyName("deployment_id")] string? DeploymentId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("capability_id")] string CapabilityId,
    [property: JsonPropertyName("input_hash")] string? InputHash,
    [property: JsonPropertyName("authorization_decision_id")] string AuthorizationDecisionId,
    [property: JsonPropertyName("policy_hash")] string PolicyHash,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("output_hash")] string? OutputHash,
    [property: JsonPropertyName("error")] string? Error);

public sealed record CompactContextRequestDto(
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("hint")] string? Hint,
    [property: JsonPropertyName("clean_history_gc")] bool CleanHistoryGc = true);

public sealed record CompactContextResponseDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("details")] object? Details);
