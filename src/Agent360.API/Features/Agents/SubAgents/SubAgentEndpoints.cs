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
            .AllowAnonymous();

        // ---- Registry ----
        agents.MapGet("subagents", async (string agentId, SubAgentStore store) =>
            Results.Ok(await store.GetBindingsAsync(SubAgentService.NormalizeAgentId(agentId))))
            .WithName("ListSubAgents");

        agents.MapPut("subagents/{childAgentId}", async (string agentId, string childAgentId,
                [FromBody] UpsertSubAgentBindingRequest body, SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                var r = await svc.UpsertBindingAsync(agentId, childAgentId, body, ct);
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

        agents.MapPost("subagents/sync-skill", async (string agentId, SubAgentService svc, CancellationToken ct) =>
            {
                var (synced, content) = await svc.SyncSkillAsync(agentId, ct);
                return synced
                    ? Results.Ok(new { synced, skill = SubAgentSkillGenerator.SkillName, content })
                    : Results.Problem("Failed to sync skill to QwenPaw runtime", statusCode: 502);
            })
            .WithName("SyncSubAgentSkill");

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
            .AllowAnonymous();

        dlg.MapPost("", async ([FromBody] CreateDelegationRequest body, SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                var r = await svc.CreateAsync(body, ct);
                return r.Decision.Allowed ? Results.Ok(r.Value) : Problem(r.Decision);
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

        dlg.MapPost("{taskId}/review", async (string taskId, [FromBody] ReviewDelegationRequest body, SubAgentService svc) =>
            {
                var r = await svc.ReviewAsync(taskId, body);
                return r.Decision.Allowed ? Results.Ok(r.Value) : Problem(r.Decision);
            })
            .WithName("ReviewDelegation");

        dlg.MapPost("{taskId}/retry", async (string taskId, [FromBody] RetryDelegationRequest body, SubAgentService svc, IOptions<SubAgentOptions> opt, CancellationToken ct) =>
            {
                if (!opt.Value.Enabled) return Disabled();
                var r = await svc.RetryAsync(taskId, body.Feedback, ct);
                return r.Decision.Allowed ? Results.Ok(r.Value) : Problem(r.Decision);
            })
            .WithName("RetryDelegation");
    }

    private static async ValueTask<object?> RuntimeUnavailableFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (QwenPawUnavailableException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 503, title: "QwenPaw runtime temporarily unavailable");
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 502, title: "QwenPaw runtime request failed");
        }
    }

    private static IResult Problem(PolicyDecision d) =>
        Results.Problem(detail: d.Reason, statusCode: d.StatusCode, title: "Sub-agent policy rejected the request");

    private static IResult Disabled() =>
        Results.Problem(detail: "Agent360:SubAgents:Enabled is false; registry and ledger are read-only.", statusCode: 503, title: "Sub-agents disabled");
}
