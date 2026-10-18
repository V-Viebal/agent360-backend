using System.Text.RegularExpressions;

namespace Agent360.API.Features.Agents.SubAgents;

public sealed record PolicyDecision(bool Allowed, string? Reason, int StatusCode = 400)
{
    public static readonly PolicyDecision Ok = new(true, null, 200);

    public static PolicyDecision Deny(string reason, int status = 400) => new(false, reason, status);
}

public static partial class SubAgentPolicy
{
    public static PolicyDecision ValidateBinding(string parentAgentId, string childAgentId, UpsertSubAgentBindingRequest request)
    {
        if (string.IsNullOrWhiteSpace(parentAgentId) || string.IsNullOrWhiteSpace(childAgentId))
            return PolicyDecision.Deny("parent and child agent ids are required");
        if (string.Equals(parentAgentId, childAgentId, StringComparison.Ordinal))
            return PolicyDecision.Deny("self-delegation is not allowed");
        if (request.AllowedModes is { Count: > 0 } modes && modes.Any(m => !DelegationModes.All.Contains(m)))
            return PolicyDecision.Deny("allowed_modes must be sync and/or async");
        if (request.MaxParallelTasks is < 1 or > 10)
            return PolicyDecision.Deny("max_parallel_tasks must be between 1 and 10");
        if (request.TimeoutSeconds is < 10 or > 3600)
            return PolicyDecision.Deny("timeout_seconds must be between 10 and 3600");
        return PolicyDecision.Ok;
    }

    public static PolicyDecision ValidateDelegation(
        CreateDelegationRequest request,
        SubAgentBinding? binding,
        IReadOnlyList<string> callChain,
        int depth,
        int maxDepth,
        int activeChildTasks)
    {
        if (string.Equals(request.ParentAgentId, request.ChildAgentId, StringComparison.Ordinal))
            return PolicyDecision.Deny("self-delegation is not allowed");
        if (binding is null)
            return PolicyDecision.Deny($"no sub-agent binding {request.ParentAgentId} -> {request.ChildAgentId}", 403);
        if (!binding.Enabled)
            return PolicyDecision.Deny("sub-agent binding is disabled", 403);
        if (!binding.AllowedModes.Contains(request.Mode))
            return PolicyDecision.Deny($"mode '{request.Mode}' is not allowed for this binding", 403);
        if (depth > Math.Min(maxDepth, binding.MaxDepth))
            return PolicyDecision.Deny($"delegation_depth {depth} exceeds max_depth {Math.Min(maxDepth, binding.MaxDepth)}", 409);
        if (callChain.Contains(request.ChildAgentId, StringComparer.Ordinal))
            return PolicyDecision.Deny($"cycle detected: {request.ChildAgentId} already in call_chain", 409);
        if (activeChildTasks >= binding.MaxParallelTasks)
            return PolicyDecision.Deny($"parallel task limit {binding.MaxParallelTasks} reached for {request.ChildAgentId}", 429);
        if (string.IsNullOrWhiteSpace(request.Objective))
            return PolicyDecision.Deny("objective is required");
        if (ContainsSecret(request.Objective) || ContainsSecret(request.Context) || ContainsSecret(request.Constraints))
            return PolicyDecision.Deny("delegation payload appears to contain a secret or credential", 422);
        return PolicyDecision.Ok;
    }

    /// <summary>Structural validation before any normalization, so malformed bodies return 400 instead of 500.</summary>
    public static PolicyDecision ValidateCreateShape(CreateDelegationRequest? r)
    {
        if (r is null) return PolicyDecision.Deny("request body is required");
        if (string.IsNullOrWhiteSpace(r.ParentAgentId)) return PolicyDecision.Deny("parent_agent_id is required");
        if (string.IsNullOrWhiteSpace(r.ChildAgentId)) return PolicyDecision.Deny("child_agent_id is required");
        if (string.IsNullOrWhiteSpace(r.Objective)) return PolicyDecision.Deny("objective is required");
        if (r.Mode is not null && !DelegationModes.All.Contains(r.Mode.ToLowerInvariant()))
            return PolicyDecision.Deny("mode must be sync or async");
        if (r.Objective.Length > 8000) return PolicyDecision.Deny("objective is too long (max 8000 chars)");
        if (r.TimeoutSeconds is < 10 or > 3600) return PolicyDecision.Deny("timeout_seconds must be between 10 and 3600");
        return PolicyDecision.Ok;
    }

    public const string QuestionMarker = "QUESTION_FOR_PARENT:";
    public const string AnswerMarker = "ANSWER_FROM_PARENT:";

    public const string ClarificationInstruction =
        "If you cannot complete the objective without a missing fact or decision that only the requesting agent has, " +
        "do not guess and do not give a partial answer: reply with exactly one line `QUESTION_FOR_PARENT: <one precise question>` and stop. " +
        "The requesting agent will answer in this same conversation with a message starting `ANSWER_FROM_PARENT:`; then finish the task. " +
        "Never call the requesting agent yourself.";

    /// <summary>Extracts the clarification question when the child ended its turn with the protocol marker.</summary>
    public static string? ExtractQuestion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var i = output.IndexOf(QuestionMarker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var q = output[(i + QuestionMarker.Length)..].Trim().Trim('`').Trim();
        var nl = q.IndexOf('\n');
        if (nl >= 0) q = q[..nl].Trim();
        return q.Length == 0 ? null : q;
    }

    /// <summary>Wraps a parent message so the child can tell answers from new instructions.</summary>
    public static string BuildParentMessage(string kind, string text) => kind == DelegationTurnKinds.Answer
        ? $"{AnswerMarker} {text.Trim()}\n\nContinue the original task with this information. {ClarificationInstruction}"
        : $"Follow-up from the requesting agent: {text.Trim()}\n\n{ClarificationInstruction}";

    public static PolicyDecision ValidateMessage(DelegationTask task, SubAgentBinding? binding, DelegationMessageRequest? body, int maxTurns)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Text)) return PolicyDecision.Deny("text is required");
        if (body.Text.Length > 8000) return PolicyDecision.Deny("text is too long (max 8000 chars)");
        if (body.Kind is not null && body.Kind is not (DelegationTurnKinds.Answer or DelegationTurnKinds.Message))
            return PolicyDecision.Deny("kind must be answer or message");
        if (binding is null || !binding.Enabled) return PolicyDecision.Deny("binding missing or disabled", 403);
        if (!binding.AllowedModes.Contains(DelegationModes.Sync))
            return PolicyDecision.Deny("conversation messages require the sync mode on this binding", 403);
        if (task.Mode != DelegationModes.Sync) return PolicyDecision.Deny("only sync delegations support conversation messages", 409);
        if (string.IsNullOrWhiteSpace(task.SessionId)) return PolicyDecision.Deny("delegation has no native session to continue", 409);
        if (task.Status is not (DelegationStatuses.AwaitingParent or DelegationStatuses.Completed or DelegationStatuses.UnderReview))
            return PolicyDecision.Deny($"cannot message a delegation in status '{task.Status}'", 409);
        if (task.Turns.Count + 2 > maxTurns) return PolicyDecision.Deny($"conversation turn budget {maxTurns} exhausted", 409);
        if (ContainsSecret(body.Text)) return PolicyDecision.Deny("message appears to contain a secret or credential", 422);
        return PolicyDecision.Ok;
    }

    public static bool ContainsSecret(string? text) => !string.IsNullOrEmpty(text) && SecretRegex().IsMatch(text);

    public static string BuildDelegationPrompt(CreateDelegationRequest r, IReadOnlyList<string> criteria, string? feedback = null)
    {
        var lines = new List<string>
        {
            "## Objective", r.Objective.Trim(), "",
            "## Context", string.IsNullOrWhiteSpace(r.Context) ? "(none)" : r.Context.Trim(), "",
            "## Constraints", string.IsNullOrWhiteSpace(r.Constraints) ? "Stay strictly within the objective. Do not perform external side effects." : r.Constraints.Trim(), "",
            "## Expected output", string.IsNullOrWhiteSpace(r.ExpectedOutput) ? "A concise, direct answer." : r.ExpectedOutput.Trim(), "",
            "## Acceptance criteria",
        };
        lines.AddRange(criteria.Count == 0 ? ["- Answer the objective directly."] : criteria.Select(c => $"- {c}"));
        lines.Add("");
        lines.Add("## Evidence required");
        lines.Add("State briefly how your answer satisfies each acceptance criterion.");
        lines.Add("");
        lines.Add("## If you need information from the requesting agent");
        lines.Add(ClarificationInstruction);
        if (!string.IsNullOrWhiteSpace(feedback))
        {
            lines.Add("");
            lines.Add("## Reviewer feedback from previous attempt (must be addressed)");
            lines.Add("This is a retry. The previous answer was rejected. Where this feedback conflicts with the sections above, the feedback takes precedence.");
            lines.Add(feedback.Trim());
        }

        return string.Join("\n", lines);
    }

    /// <summary>Rule-based acceptance: every criterion must be evidenced in the output.</summary>
    public static IReadOnlyList<AcceptanceCheck> EvaluateRules(IReadOnlyList<string> criteria, string? output)
    {
        var text = output ?? "";
        return criteria.Select(c =>
        {
            var expect = ExpectedLiteral(c);
            if (expect is not null)
            {
                var pass = text.Contains(expect, StringComparison.Ordinal);
                return new AcceptanceCheck(c, pass ? "pass" : "fail", pass ? $"output contains '{expect}'" : $"output missing '{expect}'");
            }

            var minItems = MinItems(c);
            if (minItems is not null)
            {
                var count = text.Split('\n').Count(l => ListItemRegex().IsMatch(l));
                var pass = count >= minItems;
                return new AcceptanceCheck(c, pass ? "pass" : "fail", $"found {count} list items, need {minItems}");
            }

            return new AcceptanceCheck(c, string.IsNullOrWhiteSpace(text) ? "fail" : "unknown", "requires parent-agent or human judgement");
        }).ToList();
    }

    public static AcceptanceResult Decide(string taskId, string reviewer, IReadOnlyList<string> criteria, IReadOnlyList<AcceptanceCheck> checks, string? requested, string? reason)
    {
        var byCriterion = checks.ToDictionary(c => c.Criterion, c => c, StringComparer.Ordinal);
        var normalized = criteria.Select(c => byCriterion.TryGetValue(c, out var k) ? k : new AcceptanceCheck(c, "unknown", "not evaluated")).ToList();
        var anyFail = normalized.Any(c => c.Result == "fail");
        var anyUnknown = normalized.Any(c => c.Result == "unknown");
        var passCount = normalized.Count(c => c.Result == "pass");
        var score = normalized.Count == 0 ? 1.0 : Math.Round((double)passCount / normalized.Count, 2);

        string decision;
        string why;
        if (anyFail)
        {
            decision = "rejected";
            why = reason ?? "one or more required criteria failed";
        }
        else if (requested == "rejected")
        {
            decision = "rejected";
            why = reason ?? "rejected by reviewer";
        }
        else if (anyUnknown)
        {
            decision = "rejected";
            why = "cannot accept: criteria not evaluated (" + string.Join(", ", normalized.Where(c => c.Result == "unknown").Select(c => c.Criterion)) + ")";
        }
        else
        {
            decision = "accepted";
            why = reason ?? "all required criteria passed";
        }

        return new AcceptanceResult(taskId, decision, score, normalized, reviewer, why, DateTimeOffset.UtcNow);
    }

    private static string? ExpectedLiteral(string criterion)
    {
        var m = ContainsRegex().Match(criterion);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int? MinItems(string criterion)
    {
        var m = MinItemsRegex().Match(criterion);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    [GeneratedRegex(@"(?i)(ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{20,}|ck_[A-Za-z0-9_]{12,}|xox[abp]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(password|passwd|api[_-]?key|secret|token)\s*[:=]\s*\S{6,})")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"(?i)(?:contains?|must include|includes?|chứa)\s*[:\s]\s*[`'""]([^`'""]+)[`'""]")]
    private static partial Regex ContainsRegex();

    [GeneratedRegex(@"(?i)(?:at least|minimum|tối thiểu|ít nhất)\s+(\d+)\s+(?:items?|criteria|tiêu chí|bullets?|mục)")]
    private static partial Regex MinItemsRegex();

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+\S")]
    private static partial Regex ListItemRegex();
}
