using System.Net.Http.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Schedules;

public sealed class ScheduleEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List scheduled jobs (Routines/Crons)
        routeBuilder.MapGet(
            "schedules",
            async Task<IResult> (
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                CancellationToken cancellationToken) =>
            {
                if (httpContext.User.Identity?.IsAuthenticated == true &&
                    await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, cancellationToken) is { } denied)
                {
                    return denied;
                }
                var target = routing.ResolveTarget(agentId);
                using var response = await routing.SendAsync(target.Instance!, HttpMethod.Get, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/cron/jobs", ct: cancellationToken);
                response.EnsureSuccessStatusCode();
                var jobs = await response.Content.ReadFromJsonAsync<IReadOnlyList<CronJobItemDto>>(cancellationToken);

                if (jobs is null)
                {
                    return TypedResults.Ok(Array.Empty<ScheduleJobSummaryResponse>());
                }

                var result = jobs.Select(j => new ScheduleJobSummaryResponse(
                    Id: j.Id,
                    Name: j.Name,
                    Enabled: j.Enabled,
                    CronExpression: j.Schedule?.Cron,
                    Timezone: j.Schedule?.Timezone ?? "Asia/Ho_Chi_Minh",
                    TaskType: j.TaskType ?? "agent",
                    Status: j.Enabled ? "active" : "paused"
                )).ToList();

                return TypedResults.Ok(result);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListSchedules")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all agent routines and scheduled cron jobs")
            .Produces<IReadOnlyList<ScheduleJobSummaryResponse>>()
            .AllowAnonymous();

        // 2. Trigger instant run of a scheduled job
        routeBuilder.MapPost(
            "schedules/{jobId}/run",
            async Task<IResult> (
                string jobId,
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                CancellationToken cancellationToken) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, cancellationToken) is { } denied) return denied;
                var target = routing.ResolveTarget(agentId);
                using var response = await routing.SendAsync(target.Instance!, HttpMethod.Post, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/cron/jobs/{Uri.EscapeDataString(jobId)}/run", ct: cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return TypedResults.Ok(new { jobId, status = "triggered" });
                }

                return TypedResults.Problem($"Failed to trigger scheduled job: {response.StatusCode}");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("TriggerScheduleRun")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Trigger instant execution of a routine/cron job")
            .AllowAnonymous();

        // 3. Pause / Resume schedule
        routeBuilder.MapPost(
            "schedules/{jobId}/toggle",
            async Task<IResult> (
                string jobId,
                [FromQuery] bool enable,
                [FromQuery] string? agentId,
                HttpContext httpContext,
                IFleetInstanceRoutingService routing,
                CancellationToken cancellationToken) =>
            {
                if (await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, cancellationToken) is { } denied) return denied;
                var target = routing.ResolveTarget(agentId);
                var action = enable ? "resume" : "pause";
                using var response = await routing.SendAsync(target.Instance!, HttpMethod.Post, $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/cron/jobs/{Uri.EscapeDataString(jobId)}/{action}", ct: cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return TypedResults.Ok(new { jobId, enabled = enable });
                }

                return TypedResults.Problem($"Failed to toggle schedule state: {response.StatusCode}");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ToggleScheduleState")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Pause or resume a routine/cron job")
            .AllowAnonymous();
    }
}
