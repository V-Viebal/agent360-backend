using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.SubAgents;

public sealed record ServiceResult<T>(T? Value, PolicyDecision Decision)
{
    public static ServiceResult<T> Ok(T value) => new(value, PolicyDecision.Ok);

    public static ServiceResult<T> Fail(PolicyDecision d) => new(default, d);
}

public sealed class SubAgentService(
    SubAgentStore store,
    IQwenPawA2AClient native,
    IOptions<SubAgentOptions> options,
    ILogger<SubAgentService> logger,
    ISubAgentSkillSyncQueue? skillSyncQueue = null)
{
    private readonly SubAgentOptions _opt = options.Value;

    public static string NormalizeAgentId(string id) => id.Contains(':') ? id.Split(':').Last() : id.Trim();

    public async Task<ServiceResult<SubAgentBinding>> UpsertBindingAsync(string parent, string child, UpsertSubAgentBindingRequest req, CancellationToken ct)
    {
        parent = NormalizeAgentId(parent);
        child = NormalizeAgentId(child);
        var check = SubAgentPolicy.ValidateBinding(parent, child, req);
        if (!check.Allowed) return ServiceResult<SubAgentBinding>.Fail(check);

        var agents = await native.ListAgentsAsync(ct);
        if (agents.All(a => a.Id != parent)) return ServiceResult<SubAgentBinding>.Fail(PolicyDecision.Deny($"parent agent '{parent}' not found on runtime", 404));
        var target = agents.FirstOrDefault(a => a.Id == child);
        if (target is null) return ServiceResult<SubAgentBinding>.Fail(PolicyDecision.Deny($"child agent '{child}' not found on runtime", 404));

        var existing = await store.GetBindingAsync(parent, child);
        var now = DateTimeOffset.UtcNow;
        var binding = new SubAgentBinding(
            existing?.Id ?? $"sab_{Guid.NewGuid():N}"[..16],
            parent,
            child,
            _opt.RuntimeInstanceId,
            "subagent",
            req.Role ?? existing?.Role ?? target.Name,
            req.Capabilities ?? existing?.Capabilities ?? [],
            req.AllowedModes is { Count: > 0 } m ? m : existing?.AllowedModes ?? [DelegationModes.Sync, DelegationModes.Async],
            1,
            req.MaxParallelTasks ?? existing?.MaxParallelTasks ?? 2,
            req.TimeoutSeconds ?? existing?.TimeoutSeconds ?? 300,
            req.RequiresHumanApproval ?? existing?.RequiresHumanApproval ?? false,
            (req.Enabled ?? existing?.Enabled ?? true) && target.Enabled,
            existing?.CreatedAt ?? now,
            now);
        await store.UpsertBindingAsync(binding);
        await RequestSkillSyncAsync(parent, ct);
        return ServiceResult<SubAgentBinding>.Ok(binding);
    }

    public async Task<bool> DeleteBindingAsync(string parent, string child, CancellationToken ct)
    {
        parent = NormalizeAgentId(parent);
        var removed = await store.DeleteBindingAsync(parent, NormalizeAgentId(child));
        if (removed) await RequestSkillSyncAsync(parent, ct);
        return removed;
    }

    /// <summary>Queue the skill push off the request path when a background queue is registered; otherwise sync inline.</summary>
    private async Task RequestSkillSyncAsync(string parent, CancellationToken ct)
    {
        if (skillSyncQueue is not null) skillSyncQueue.Enqueue(parent);
        else await SyncSkillAsync(parent, ct);
    }

    public async Task<(bool Synced, string Content)> SyncSkillAsync(string parent, CancellationToken ct)
    {
        parent = NormalizeAgentId(parent);
        var content = SubAgentSkillGenerator.Generate(parent, await store.GetBindingsAsync(parent));
        var ok = await native.UpsertSkillAsync(parent, SubAgentSkillGenerator.SkillName, content, ct);
        if (!ok) logger.LogWarning("Failed to sync {Skill} into agent {Agent}", SubAgentSkillGenerator.SkillName, parent);
        return (ok, content);
    }

    public async Task<IReadOnlyList<object>> HealthAsync(string parent, CancellationToken ct)
    {
        parent = NormalizeAgentId(parent);
        var agents = (await native.ListAgentsAsync(ct)).ToDictionary(a => a.Id);
        var tasks = await store.GetTasksAsync(parent);
        return (await store.GetBindingsAsync(parent)).Select(b => (object)new
        {
            child_agent_id = b.ChildAgentId,
            binding_enabled = b.Enabled,
            runtime_present = agents.ContainsKey(b.ChildAgentId),
            runtime_enabled = agents.TryGetValue(b.ChildAgentId, out var a) && a.Enabled,
            callable = b.Enabled && agents.TryGetValue(b.ChildAgentId, out var a2) && a2.Enabled,
            active_tasks = tasks.Count(t => t.ChildAgentId == b.ChildAgentId && t.Status is DelegationStatuses.Queued or DelegationStatuses.Running or DelegationStatuses.RetryQueued),
            accepted_tasks = tasks.Count(t => t.ChildAgentId == b.ChildAgentId && t.Status == DelegationStatuses.Accepted),
            failed_tasks = tasks.Count(t => t.ChildAgentId == b.ChildAgentId && t.Status is DelegationStatuses.Failed or DelegationStatuses.TimedOut),
        }).ToList();
    }

    public async Task<NativeChatResult> ProbeAsync(string parent, string child, string? prompt, CancellationToken ct)
    {
        parent = NormalizeAgentId(parent);
        child = NormalizeAgentId(child);
        var binding = await store.GetBindingAsync(parent, child);
        if (binding is null || !binding.Enabled)
            return new NativeChatResult(false, "", null, "binding missing or disabled", 403);
        return await native.ChatAsync(parent, child, prompt ?? "Reply with exactly: AGENT360_PROBE_OK", Math.Min(binding.TimeoutSeconds, 90), ct);
    }

    public async Task<ServiceResult<DelegationTask>> CreateAsync(CreateDelegationRequest req, CancellationToken ct)
    {
        req = req with
        {
            ParentAgentId = NormalizeAgentId(req.ParentAgentId),
            ChildAgentId = NormalizeAgentId(req.ChildAgentId),
            Mode = (req.Mode ?? DelegationModes.Sync).ToLowerInvariant(),
        };
        var binding = await store.GetBindingAsync(req.ParentAgentId, req.ChildAgentId);
        var chain = (req.CallChain is { Count: > 0 } c ? c : [req.ParentAgentId]).ToList();
        if (!chain.Contains(req.ParentAgentId)) chain.Add(req.ParentAgentId);
        var depth = chain.Count;
        var active = (await store.GetTasksAsync()).Count(t => t.ChildAgentId == req.ChildAgentId
            && t.Status is DelegationStatuses.Queued or DelegationStatuses.Running or DelegationStatuses.RetryQueued);
        var decision = SubAgentPolicy.ValidateDelegation(req, binding, chain, depth, _opt.MaxDepth, active);
        if (!decision.Allowed) return ServiceResult<DelegationTask>.Fail(decision);

        var now = DateTimeOffset.UtcNow;
        var runId = req.ParentRunId ?? $"run_{Guid.NewGuid():N}"[..16];
        var criteria = req.AcceptanceCriteria ?? [];
        var task = new DelegationTask(
            $"dlg_{Guid.NewGuid():N}"[..16], runId, req.RootRunId ?? runId, req.ParentTaskId, depth, [.. chain, req.ChildAgentId],
            req.ParentAgentId, req.ChildAgentId, req.Mode, "agent360", null, "", req.Objective,
            Summarize(req.Context), criteria, DelegationStatuses.Queued, 1, _opt.MaxAttempts, null, null, [], null, [], now, now, null);
        await store.SaveTaskAsync(task);
        return ServiceResult<DelegationTask>.Ok(await DispatchAsync(task, binding!, SubAgentPolicy.BuildDelegationPrompt(req, criteria), req.TimeoutSeconds, ct));
    }

    public async Task<DelegationTask?> SyncNativeStatusAsync(string taskId, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(taskId);
        if (task is null || task.NativeTaskId is null || task.Status is not (DelegationStatuses.Running or DelegationStatuses.Queued or DelegationStatuses.RetryQueued))
            return task;

        var st = await native.GetTaskAsync(task.NativeTaskId, task.ChildAgentId, ct);
        var now = DateTimeOffset.UtcNow;
        var evidence = task.Evidence.Append($"native_status:{st.Status}/{st.ResultStatus}@{now:O}").TakeLast(20).ToList();
        if (st.Status != "finished")
        {
            var running = st.Status is "running" or "pending" or "submitted" ? DelegationStatuses.Running : task.Status;
            if (st.StatusCode == 404) running = DelegationStatuses.Failed;
            return await store.SaveTaskAsync(task with { Status = running, Evidence = evidence, Error = st.Error ?? task.Error, UpdatedAt = now });
        }

        var status = st.ResultStatus switch
        {
            "completed" => DelegationStatuses.Completed,
            _ when (st.Error ?? "").Contains("timeout", StringComparison.OrdinalIgnoreCase) => DelegationStatuses.TimedOut,
            _ => DelegationStatuses.Failed,
        };
        return await AutoReviewAsync(await store.SaveTaskAsync(task with
        {
            Status = status, Output = st.Text, Error = st.Error, Evidence = evidence, UpdatedAt = now, CompletedAt = now,
        }));
    }

    public async Task<ServiceResult<DelegationTask>> ReviewAsync(string taskId, ReviewDelegationRequest req)
    {
        var task = await store.GetTaskAsync(taskId);
        if (task is null) return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny("delegation not found", 404));
        if (task.Status is not (DelegationStatuses.Completed or DelegationStatuses.UnderReview))
            return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny($"cannot review task in status '{task.Status}'", 409));

        var reviewer = req.Reviewer is "parent-agent" or "human" or "rule-based" ? req.Reviewer : "human";
        var checks = reviewer == "rule-based" || req.Checks is null
            ? SubAgentPolicy.EvaluateRules(task.AcceptanceCriteria, task.Output)
            : req.Checks;
        var result = SubAgentPolicy.Decide(task.Id, reviewer!, task.AcceptanceCriteria, checks, req.Decision, req.Reason);
        var status = result.Decision == "accepted" ? DelegationStatuses.Accepted : DelegationStatuses.Rejected;
        return ServiceResult<DelegationTask>.Ok(await store.SaveTaskAsync(task with
        {
            Status = status, Acceptance = result, ReviewHistory = [.. task.ReviewHistory, result], UpdatedAt = DateTimeOffset.UtcNow,
        }));
    }

    public async Task<ServiceResult<DelegationTask>> RetryAsync(string taskId, string feedback, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(taskId);
        if (task is null) return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny("delegation not found", 404));
        if (task.Status != DelegationStatuses.Rejected)
            return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny("only rejected delegations can be retried", 409));
        if (task.Attempt >= task.MaxAttempts)
            return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny($"max_attempts {task.MaxAttempts} reached", 409));
        if (string.IsNullOrWhiteSpace(feedback))
            return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny("feedback is required for retry"));
        var binding = await store.GetBindingAsync(task.ParentAgentId, task.ChildAgentId);
        if (binding is null || !binding.Enabled)
            return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny("binding missing or disabled", 403));

        var retry = await store.SaveTaskAsync(task with
        {
            Status = DelegationStatuses.RetryQueued, Attempt = task.Attempt + 1, Output = null, Error = null,
            Acceptance = null, NativeTaskId = null, UpdatedAt = DateTimeOffset.UtcNow, CompletedAt = null,
        });
        var req = new CreateDelegationRequest(task.ParentAgentId, task.ChildAgentId, task.Mode, task.Objective, task.InputSummary,
            null, null, task.AcceptanceCriteria, task.ParentRunId, task.RootRunId, task.ParentTaskId, task.CallChain, null);
        return ServiceResult<DelegationTask>.Ok(await DispatchAsync(retry, binding, SubAgentPolicy.BuildDelegationPrompt(req, task.AcceptanceCriteria, feedback), null, ct));
    }

    public async Task<ServiceResult<DelegationTask>> CancelAsync(string taskId, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(taskId);
        if (task is null) return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny("delegation not found", 404));
        if (task.Status is not (DelegationStatuses.Queued or DelegationStatuses.Running or DelegationStatuses.RetryQueued))
            return ServiceResult<DelegationTask>.Fail(PolicyDecision.Deny($"cannot cancel task in status '{task.Status}'", 409));
        var stopped = !string.IsNullOrEmpty(task.SessionId) && await native.StopChatAsync(task.SessionId, task.ChildAgentId, ct);
        return ServiceResult<DelegationTask>.Ok(await store.SaveTaskAsync(task with
        {
            Status = DelegationStatuses.Cancelled,
            Evidence = [.. task.Evidence, $"native_stop:{stopped}"],
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        }));
    }

    /// <summary>Phase 4: ingest autonomous chat_with_agent/submit_to_agent tool calls from the parent's chat history.</summary>
    public async Task<IReadOnlyList<DelegationTask>> IngestNativeAsync(string parent, int maxChats, CancellationToken ct)
    {
        parent = NormalizeAgentId(parent);
        var detected = await native.ScanAgentDelegationsAsync(parent, maxChats, ct);
        var ingested = new List<DelegationTask>();
        foreach (var d in detected)
        {
            if (await store.FindByNativeKeyAsync(parent, d.CallId) is not null) continue;
            var binding = await store.GetBindingAsync(parent, d.ChildAgentId);
            var mode = d.ToolName == "submit_to_agent" ? DelegationModes.Async : DelegationModes.Sync;
            var policyNote = binding is null ? "policy_violation:unbound_child" : !binding.Enabled ? "policy_violation:binding_disabled" : "policy:bound";
            var at = d.ObservedAt ?? DateTimeOffset.UtcNow;
            var outputText = StripTags(d.Output);
            var status = mode == DelegationModes.Async
                ? (d.NativeTaskId is null ? DelegationStatuses.Failed : DelegationStatuses.Running)
                : (string.IsNullOrWhiteSpace(outputText) || outputText.StartsWith("ERROR", StringComparison.Ordinal) ? DelegationStatuses.Failed : DelegationStatuses.Completed);
            var task = new DelegationTask(
                $"dlg_{Guid.NewGuid():N}"[..16], d.ChatId, d.ChatId, null, 1, [parent, d.ChildAgentId], parent, d.ChildAgentId, mode,
                "native-tool-call", d.NativeTaskId, d.NativeSessionId ?? "", Truncate(d.Text, 500), null, [], status, 1, 1,
                mode == DelegationModes.Sync ? outputText : null, status == DelegationStatuses.Failed ? Truncate(outputText, 300) : null,
                [$"call_id:{d.CallId}", $"tool:{d.ToolName}", $"chat_id:{d.ChatId}", policyNote], null, [], at, DateTimeOffset.UtcNow,
                status == DelegationStatuses.Completed ? at : null);
            ingested.Add(await store.SaveTaskAsync(task));
        }

        return ingested;
    }

    private async Task<DelegationTask> DispatchAsync(DelegationTask task, SubAgentBinding binding, string prompt, int? timeoutOverride, CancellationToken ct)
    {
        var timeout = Math.Clamp(timeoutOverride ?? binding.TimeoutSeconds, 10, binding.TimeoutSeconds);
        var now = DateTimeOffset.UtcNow;
        if (task.Mode == DelegationModes.Sync)
        {
            await store.SaveTaskAsync(task with { Status = DelegationStatuses.Running, UpdatedAt = now });
            var r = await native.ChatAsync(task.ParentAgentId, task.ChildAgentId, prompt, timeout, ct);
            var status = r.Success ? DelegationStatuses.Completed : r.Error == "timed_out" ? DelegationStatuses.TimedOut : DelegationStatuses.Failed;
            var done = await store.SaveTaskAsync(task with
            {
                Status = status, SessionId = r.SessionId, Output = r.Text, Error = r.Error,
                Evidence = [.. task.Evidence, $"native:console/chat status={r.StatusCode} session={r.SessionId}"],
                UpdatedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            });
            return await AutoReviewAsync(done);
        }

        var s = await native.SubmitAsync(task.ParentAgentId, task.ChildAgentId, prompt, timeout, ct);
        return await store.SaveTaskAsync(task with
        {
            Status = s.Success ? DelegationStatuses.Running : DelegationStatuses.Failed,
            NativeTaskId = s.NativeTaskId, SessionId = s.SessionId, Error = s.Error,
            Evidence = [.. task.Evidence, $"native:console/chat/task status={s.StatusCode} task_id={s.NativeTaskId}"],
            UpdatedAt = DateTimeOffset.UtcNow, CompletedAt = s.Success ? null : DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Completed tasks with criteria move to under_review with a rule-based pre-check; they are never auto-accepted.</summary>
    private async Task<DelegationTask> AutoReviewAsync(DelegationTask task)
    {
        if (task.Status != DelegationStatuses.Completed || task.AcceptanceCriteria.Count == 0) return task;
        var checks = SubAgentPolicy.EvaluateRules(task.AcceptanceCriteria, task.Output);
        var pre = SubAgentPolicy.Decide(task.Id, "rule-based", task.AcceptanceCriteria, checks, null, "automatic pre-check");
        return await store.SaveTaskAsync(task with
        {
            Status = DelegationStatuses.UnderReview, Acceptance = pre, ReviewHistory = [.. task.ReviewHistory, pre], UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static string? StripTags(string? output) =>
        output is null ? null : System.Text.RegularExpressions.Regex.Replace(output, @"^\[(SESSION|TASK_ID|TIMEOUT|STATUS):[^\]]*\]\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();

    private static string? Summarize(string? text) => text is null ? null : Truncate(text, 500);

    private static string Truncate(string? text, int max) => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];
}
