using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Approvals;

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
