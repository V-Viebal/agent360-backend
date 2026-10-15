using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Approvals;

public sealed record ApprovalItemDto(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("created_at")] double CreatedAt,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds,
    [property: JsonPropertyName("result_summary")] string? ResultSummary,
    [property: JsonPropertyName("reasoning")] string? Reasoning);

public sealed record ApprovalListApiResponse(
    [property: JsonPropertyName("pending_approvals")] IReadOnlyList<ApprovalItemDto> PendingApprovals,
    [property: JsonPropertyName("count")] int Count);

public sealed record ApprovalDecisionRequest(
    string RequestId,
    string SessionId,
    bool Approve,
    string? Reason = null,
    string? Scope = "exact");

public sealed record ApprovalDecisionResponse(
    bool Success,
    string Message,
    string RequestId,
    string Decision);
