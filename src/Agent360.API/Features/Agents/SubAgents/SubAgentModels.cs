using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.SubAgents;

public sealed class SubAgentOptions
{
    public const string Section = "Agent360:SubAgents";

    public bool Enabled { get; set; } = true;

    public string StoragePath { get; set; } = "_storage/subagents";

    /// <summary>"file" (JSON under StoragePath) or "postgres" (tables in <see cref="Schema"/>).</summary>
    public string Storage { get; set; } = "file";

    public string ConnectionStringName { get; set; } = "DefaultConnection";

    public string Schema { get; set; } = "agent360_subagents";

    public string RuntimeInstanceId { get; set; } = "qwenpaw-core-3rd";

    public int MaxDepth { get; set; } = 1;

    public int MaxAttempts { get; set; } = 2;

    /// <summary>Background monitor: polls running async tasks and enforces timeouts.</summary>
    public bool MonitorEnabled { get; set; } = true;

    public int MonitorIntervalSeconds { get; set; } = 30;

    /// <summary>Extra time beyond the binding timeout before a running task is marked timed_out.</summary>
    public int TimeoutGraceSeconds { get; set; } = 90;

    /// <summary>Phase 4 auto-ledger: scan bound parents' recent chats for native A2A tool calls (0 disables).</summary>
    public int IngestIntervalSeconds { get; set; } = 180;

    public int IngestMaxChats { get; set; } = 10;

    /// <summary>Ledger retention: terminal tasks older than this are archived to monthly JSONL files.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Hard cap on in-ledger tasks; oldest terminal tasks are archived beyond it.</summary>
    public int MaxLedgerTasks { get; set; } = 2000;

    /// <summary>
    /// Longest time a create/retry request waits for a sync turn before answering 202 and finishing in the
    /// background (keeps responses under edge proxy limits such as Cloudflare's 100s).
    /// </summary>
    public int SyncWaitSeconds { get; set; } = 85;

    /// <summary>Multi-turn: max turns (request + questions + answers + replies) per delegation conversation.</summary>
    public int MaxConversationTurns { get; set; } = 6;

    /// <summary>Multi-turn: a delegation left in awaiting_parent longer than this is timed out by the monitor.</summary>
    public int AwaitingParentTimeoutSeconds { get; set; } = 1800;
}

public static class DelegationModes
{
    public const string Sync = "sync";
    public const string Async = "async";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Sync, Async };
}

public static class DelegationStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string AwaitingParent = "awaiting_parent";
    public const string Completed = "completed";
    public const string UnderReview = "under_review";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string RetryQueued = "retry_queued";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Accepted, Failed, TimedOut, Cancelled
    };

    private static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        [Queued] = [Running, Completed, AwaitingParent, Failed, TimedOut, Cancelled],
        [Running] = [Completed, AwaitingParent, Failed, TimedOut, Cancelled],
        [AwaitingParent] = [Running, Completed, Failed, TimedOut, Cancelled],
        [Completed] = [UnderReview, Accepted, Rejected, Running],
        [UnderReview] = [Accepted, Rejected, Running],
        [Rejected] = [RetryQueued],
        [RetryQueued] = [Running, Completed, Failed, TimedOut, Cancelled],
    };

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var next) && next.Contains(to);
}

public sealed record SubAgentBinding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parent_agent_id")] string ParentAgentId,
    [property: JsonPropertyName("child_agent_id")] string ChildAgentId,
    [property: JsonPropertyName("runtime_instance_id")] string RuntimeInstanceId,
    [property: JsonPropertyName("relationship")] string Relationship,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("allowed_modes")] IReadOnlyList<string> AllowedModes,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    [property: JsonPropertyName("max_parallel_tasks")] int MaxParallelTasks,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds,
    [property: JsonPropertyName("requires_human_approval")] bool RequiresHumanApproval,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record UpsertSubAgentBindingRequest(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities,
    [property: JsonPropertyName("allowed_modes")] IReadOnlyList<string>? AllowedModes,
    [property: JsonPropertyName("max_parallel_tasks")] int? MaxParallelTasks,
    [property: JsonPropertyName("timeout_seconds")] int? TimeoutSeconds,
    [property: JsonPropertyName("requires_human_approval")] bool? RequiresHumanApproval,
    [property: JsonPropertyName("enabled")] bool? Enabled);

public sealed record AcceptanceCheck(
    [property: JsonPropertyName("criterion")] string Criterion,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("evidence")] string? Evidence);

public sealed record AcceptanceResult(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("checks")] IReadOnlyList<AcceptanceCheck> Checks,
    [property: JsonPropertyName("reviewer")] string Reviewer,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record DelegationTask(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parent_run_id")] string ParentRunId,
    [property: JsonPropertyName("root_run_id")] string RootRunId,
    [property: JsonPropertyName("parent_task_id")] string? ParentTaskId,
    [property: JsonPropertyName("delegation_depth")] int DelegationDepth,
    [property: JsonPropertyName("call_chain")] IReadOnlyList<string> CallChain,
    [property: JsonPropertyName("parent_agent_id")] string ParentAgentId,
    [property: JsonPropertyName("child_agent_id")] string ChildAgentId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("native_task_id")] string? NativeTaskId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("objective")] string Objective,
    [property: JsonPropertyName("input_summary")] string? InputSummary,
    [property: JsonPropertyName("acceptance_criteria")] IReadOnlyList<string> AcceptanceCriteria,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("max_attempts")] int MaxAttempts,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence,
    [property: JsonPropertyName("acceptance")] AcceptanceResult? Acceptance,
    [property: JsonPropertyName("history")] IReadOnlyList<AcceptanceResult> ReviewHistory,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt)
{
    /// <summary>Ordered conversation turns (multi-turn delegations). Empty for one-shot legacy records.</summary>
    [JsonPropertyName("turns")]
    public IReadOnlyList<DelegationTurn> Turns { get; init; } = [];
}

/// <summary>One message in a delegation conversation. kind: request | question | answer | message | reply | final.</summary>
public sealed record DelegationTurn(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("call_id")] string? CallId = null);

public static class DelegationTurnKinds
{
    public const string Request = "request";
    public const string Question = "question";
    public const string Answer = "answer";
    public const string Message = "message";
    public const string Reply = "reply";
    public const string Final = "final";
}

public sealed record DelegationMessageRequest(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("kind")] string? Kind);

public sealed record CreateDelegationRequest(
    [property: JsonPropertyName("parent_agent_id")] string ParentAgentId,
    [property: JsonPropertyName("child_agent_id")] string ChildAgentId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("objective")] string Objective,
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("constraints")] string? Constraints,
    [property: JsonPropertyName("expected_output")] string? ExpectedOutput,
    [property: JsonPropertyName("acceptance_criteria")] IReadOnlyList<string>? AcceptanceCriteria,
    [property: JsonPropertyName("parent_run_id")] string? ParentRunId,
    [property: JsonPropertyName("root_run_id")] string? RootRunId,
    [property: JsonPropertyName("parent_task_id")] string? ParentTaskId,
    [property: JsonPropertyName("call_chain")] IReadOnlyList<string>? CallChain,
    [property: JsonPropertyName("timeout_seconds")] int? TimeoutSeconds);

public sealed record ReviewDelegationRequest(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("reviewer")] string? Reviewer,
    [property: JsonPropertyName("checks")] IReadOnlyList<AcceptanceCheck>? Checks,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record RetryDelegationRequest(
    [property: JsonPropertyName("feedback")] string? Feedback,
    [property: JsonPropertyName("objective")] string? Objective = null,
    [property: JsonPropertyName("expected_output")] string? ExpectedOutput = null,
    [property: JsonPropertyName("acceptance_criteria")] IReadOnlyList<string>? AcceptanceCriteria = null);

public sealed record ProbeSubAgentRequest(
    [property: JsonPropertyName("prompt")] string? Prompt);

public sealed record NativeChatResult(
    bool Success,
    string SessionId,
    string? Text,
    string? Error,
    int StatusCode);

public sealed record NativeSubmitResult(
    bool Success,
    string SessionId,
    string? NativeTaskId,
    int? TimeoutSeconds,
    string? Error,
    int StatusCode);

public sealed record NativeTaskStatus(
    string Status,
    string? ResultStatus,
    string? Text,
    string? Error,
    string? SessionId,
    int StatusCode);

public sealed record NativeAgentInfo(string Id, string Name, bool Enabled);

public sealed record DetectedNativeDelegation(
    string CallId,
    string ToolName,
    string ParentAgentId,
    string ChildAgentId,
    string Text,
    string? NativeSessionId,
    string? NativeTaskId,
    string? Output,
    string ChatId,
    DateTimeOffset? ObservedAt);
