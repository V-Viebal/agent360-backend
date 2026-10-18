using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.SubAgents;

public sealed class SubAgentEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var agents = routeBuilder.MapGroup("agents/{agentId}")
            .WithTags(EndpointTags.Agents)
            .MapToApiVersion(ApiVersion.Default)
            .AddEndpointFilter(RuntimeUnavailableFilter)
            .AddEndpointFilter<OperatorAuthFilter>()
            .AllowAnonymous();

        // ---- Registry ----
        agents.MapGet("subagents", async (string agentId, SubAgentStore store) =>
            Results.Ok(await store.GetBindingsAsync(SubAgentService.NormalizeAgentId(agentId))))
            .WithName("ListSubAgents");

        agents.MapPut("subagents/{childAgentId}", async (string agentId, string childAgentId,
                [FromBody] UpsertSubAgentBindingRequest? body, SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                var r = await svc.UpsertBindingAsync(agentId, childAgentId, body ?? new UpsertSubAgentBindingRequest(null, null, null, null, null, null, null), ct);
                return r.Decision.Allowed ? Results.Ok(r.Value) : Problem(r.Decision);
            })
            .WithName("UpsertSubAgent");

        agents.MapDelete("subagents/{childAgentId}", async (string agentId, string childAgentId,
                SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                return await svc.DeleteBindingAsync(agentId, childAgentId, ct)
                    ? Results.Ok(new { removed = true, parent_agent_id = agentId, child_agent_id = childAgentId })
                    : Results.NotFound(new { message = "binding not found" });
            })
            .WithName("DeleteSubAgent");

        agents.MapPost("subagents/sync-skill", async (string agentId, [FromQuery] bool? wait, SubAgentService svc, ISubAgentSkillSyncQueue queue, CancellationToken ct) =>
            {
                if (wait == false) return Results.Accepted(value: queue.Enqueue(agentId));
                var (synced, content) = await svc.SyncSkillAsync(agentId, ct);
                return synced
                    ? Results.Ok(new { synced, skill = SubAgentSkillGenerator.SkillName, content })
                    : Results.Problem("Failed to sync skill to QwenPaw runtime", statusCode: 502);
            })
            .WithName("SyncSubAgentSkill");

        agents.MapGet("subagents/skill-status", (string agentId, ISubAgentSkillSyncQueue queue) =>
            Results.Ok(queue.GetStatus(agentId)))
            .WithName("SubAgentSkillSyncStatus");

        agents.MapGet("subagents/health", async (string agentId, SubAgentService svc, CancellationToken ct) =>
            Results.Ok(await svc.HealthAsync(agentId, ct)))
            .WithName("SubAgentHealth");

        agents.MapPost("subagents/{childAgentId}/probe", async (string agentId, string childAgentId,
                [FromBody] ProbeSubAgentRequest? body, SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                var r = await svc.ProbeAsync(agentId, childAgentId, body?.Prompt, ct);
                return r.Success
                    ? Results.Ok(new { ok = true, session_id = r.SessionId, output = r.Text })
                    : Results.Json(new { ok = false, session_id = r.SessionId, error = r.Error }, statusCode: r.StatusCode >= 400 ? r.StatusCode : 502);
            })
            .WithName("ProbeSubAgent");

        agents.MapGet("delegations", async (string agentId, SubAgentStore store) =>
            Results.Ok(await store.GetTasksAsync(SubAgentService.NormalizeAgentId(agentId))))
            .WithName("ListAgentDelegations");

        agents.MapPost("delegations/ingest-native", async (string agentId, [FromQuery] int? maxChats,
                SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                var ingested = await svc.IngestNativeAsync(agentId, Math.Clamp(maxChats ?? 20, 1, 100), ct);
                return Results.Ok(new { ingested = ingested.Count, tasks = ingested });
            })
            .WithName("IngestNativeDelegations");

        // ---- Delegation ledger ----
        var dlg = routeBuilder.MapGroup("delegations")
            .WithTags(EndpointTags.Agents)
            .MapToApiVersion(ApiVersion.Default)
            .AddEndpointFilter(RuntimeUnavailableFilter)
            .AddEndpointFilter<OperatorAuthFilter>()
            .AllowAnonymous();

        dlg.MapPost("", async ([FromBody] CreateDelegationRequest? body, IServiceScopeFactory scopes, SubAgentStore store, IOptions<SubAgentOptions> opt,
                ILogger<SubAgentEndpoints> logger) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                if (SubAgentPolicy.ValidateCreateShape(body) is { Allowed: false } shape) return Problem(shape);
                var id = SubAgentService.NewTaskId();
                // The native turn must not be cancelled when the client/edge gives up; it runs to completion
                // and the ledger records the outcome either way.
                return await AwaitOrDetach(InOwnScope(scopes, svc => svc.CreateAsync(body!, id, CancellationToken.None)), id, store, opt.Value, logger);
            })
            .WithName("CreateDelegation");

        dlg.MapGet("{taskId}", async (string taskId, SubAgentStore store) =>
            await store.GetTaskAsync(taskId) is { } t ? Results.Ok(t) : Results.NotFound(new { message = "delegation not found" }))
            .WithName("GetDelegation");

        dlg.MapPost("{taskId}/sync-native-status", async (string taskId, SubAgentService svc, CancellationToken ct) =>
            await svc.SyncNativeStatusAsync(taskId, ct) is { } t ? Results.Ok(t) : Results.NotFound(new { message = "delegation not found" }))
            .WithName("SyncDelegationNativeStatus");

        dlg.MapPost("{taskId}/cancel", async (string taskId, SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                var r = await svc.CancelAsync(taskId, ct);
                return r.Decision.Allowed ? Results.Ok(r.Value) : Problem(r.Decision);
            })
            .WithName("CancelDelegation");

        dlg.MapPost("{taskId}/review", async (string taskId, [FromBody] ReviewDelegationRequest? body, SubAgentService svc) =>
            {
                if (body is null) return Problem(PolicyDecision.Deny("request body is required"));
                var r = await svc.ReviewAsync(taskId, body);
                return r.Decision.Allowed ? Results.Ok(r.Value) : Problem(r.Decision);
            })
            .WithName("ReviewDelegation");

        dlg.MapPost("{taskId}/messages", async (string taskId, [FromBody] DelegationMessageRequest? body, IServiceScopeFactory scopes,
                SubAgentStore store, IOptions<SubAgentOptions> opt, ILogger<SubAgentEndpoints> logger) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                if (body is null || string.IsNullOrWhiteSpace(body.Text)) return Problem(PolicyDecision.Deny("text is required"));
                return await AwaitOrDetach(InOwnScope(scopes, svc => svc.SendMessageAsync(taskId, body, CancellationToken.None)), taskId, store, opt.Value, logger);
            })
            .WithName("SendDelegationMessage")
            .WithSummary("Parent answers a sub-agent question (awaiting_parent) or sends a follow-up in the same native session");

        dlg.MapGet("{taskId}/turns", async (string taskId, SubAgentStore store) =>
            await store.GetTaskAsync(taskId) is { } t
                ? Results.Ok(new { id = t.Id, status = t.Status, session_id = t.SessionId, turns = t.Turns })
                : Results.NotFound(new { message = "delegation not found" }))
            .WithName("GetDelegationTurns");

        dlg.MapPost("{taskId}/retry", async (string taskId, [FromBody] RetryDelegationRequest? body, IServiceScopeFactory scopes, SubAgentStore store,
                IOptions<SubAgentOptions> opt, ILogger<SubAgentEndpoints> logger) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                if (body is null) return Problem(PolicyDecision.Deny("request body is required"));
                return await AwaitOrDetach(InOwnScope(scopes, svc => svc.RetryAsync(taskId, body, CancellationToken.None)), taskId, store, opt.Value, logger);
            })
            .WithName("RetryDelegation");
    }

    private static async ValueTask<object?> RuntimeUnavailableFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (BadHttpRequestException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid request");
        }
        catch (QwenPawUnavailableException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 503, title: "QwenPaw runtime temporarily unavailable");
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 502, title: "QwenPaw runtime request failed");
        }
        catch (OperationCanceledException) when (!ctx.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return Results.Problem(detail: "QwenPaw runtime did not respond in time; retry or use ?wait=false and poll subagents/skill-status.", statusCode: 504, title: "QwenPaw runtime timed out");
        }
    }

    /// <summary>Run work in a dedicated DI scope that lives until the work finishes, independent of the HTTP request.</summary>
    private static async Task<ServiceResult<DelegationTask>> InOwnScope(IServiceScopeFactory scopes, Func<SubAgentService, Task<ServiceResult<DelegationTask>>> work)
    {
        await Task.Yield();
        await using var scope = scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<SubAgentService>());
    }

    /// <summary>
    /// Wait up to SyncWaitSeconds for the dispatch; if it is still running, answer 202 with the current ledger
    /// record and let the turn finish in the background (poll GET /delegations/{id}).
    /// </summary>
    private static async Task<IResult> AwaitOrDetach(Task<ServiceResult<DelegationTask>> work, string taskId, SubAgentStore store,
        SubAgentOptions opt, ILogger logger)
    {
        var wait = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opt.SyncWaitSeconds)));
        if (await Task.WhenAny(work, wait) == work)
        {
            var r = await work;
            return r.Decision.Allowed ? Results.Ok(r.Value) : Problem(r.Decision);
        }
        _ = work.ContinueWith(t => logger.LogWarning(t.Exception, "Detached delegation {Task} failed", taskId),
            TaskContinuationOptions.OnlyOnFaulted);
        var current = await store.GetTaskAsync(taskId);
        return current is null
            ? Results.Accepted($"/api/v1/delegations/{taskId}", new { id = taskId, status = DelegationStatuses.Queued })
            : Results.Accepted($"/api/v1/delegations/{taskId}", current);
    }

    private static IResult Problem(PolicyDecision d) =>
        Results.Problem(detail: d.Reason, statusCode: d.StatusCode, title: "Sub-agent policy rejected the request");

    private static IResult Disabled() =>
        Results.Problem(detail: "Agent360:SubAgents:Enabled is false; registry and ledger are read-only.", statusCode: 503, title: "Sub-agents disabled");
}
