using System.Net;
using System.Net.Http.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Approvals;

public sealed class ApprovalManagementEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List pending approvals across every QwenPaw runtime in the fleet
        routeBuilder.MapGet(
            "approvals",
            async Task<IResult> ([FromQuery] string? sessionId, IQwenPawClient client, CancellationToken cancellationToken) =>
                TypedResults.Ok(await client.GetApprovalsAsync(sessionId, cancellationToken)))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListPendingApprovals")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all pending security/tool approvals for agents")
            .AllowAnonymous();

        // 1b. Realtime SSE stream of pending approvals
        routeBuilder.MapGet(
            "approvals/stream",
            async ([FromQuery] string? sessionId, HttpContext context, IQwenPawClient client, CancellationToken cancellationToken) =>
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var response = await client.GetApprovalsAsync(sessionId, cancellationToken);
                        var payload = System.Text.Json.JsonSerializer.Serialize(response);
                        await context.Response.WriteAsync($"event: approvals_update\ndata: {payload}\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                        await Task.Delay(2000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("StreamPendingApprovals")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Realtime SSE stream of pending tool approvals")
            .AllowAnonymous();

        // 1c/1d. Direct approve / deny
        routeBuilder.MapPost(
            "approvals/approve",
            ([FromBody] ApprovalDecisionRequest request, IQwenPawClient client, CancellationToken ct) => DecideAsync(client, request with { Approve = true }, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ApprovePendingTool")
            .WithTags(EndpointTags.Agents)
            .AllowAnonymous();

        routeBuilder.MapPost(
            "approvals/deny",
            ([FromBody] ApprovalDecisionRequest request, IQwenPawClient client, CancellationToken ct) => DecideAsync(client, request with { Approve = false }, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("DenyPendingTool")
            .WithTags(EndpointTags.Agents)
            .AllowAnonymous();

        // 2. Submit approval decision (approve or deny)
        routeBuilder.MapPost(
            "approvals/decision",
            ([FromBody] ApprovalDecisionRequest request, IQwenPawClient client, CancellationToken ct) => DecideAsync(client, request, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SubmitApprovalDecision")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Approve or deny an agent tool execution request")
            .AllowAnonymous();
    }

    private static async Task<IResult> DecideAsync(IQwenPawClient client, ApprovalDecisionRequest request, CancellationToken ct)
    {
        var dto = new ApprovalActionRequestDto(
            request.RequestId,
            request.SessionId,
            null,
            request.Approve ? request.Reason : request.Reason ?? "User denied via Agent360 Console",
            request.Scope ?? "exact");
        try
        {
            var result = request.Approve ? await client.ApproveToolAsync(dto, ct) : await client.DenyToolAsync(dto, ct);
            return TypedResults.Ok(new ApprovalDecisionResponse(
                Success: result?.Success ?? true,
                Message: result?.Message ?? (request.Approve ? "Approval granted" : "Approval denied"),
                RequestId: request.RequestId,
                Decision: request.Approve ? "approved" : "denied"));
        }
        catch (HttpRequestException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: (int)(ex.StatusCode ?? HttpStatusCode.BadGateway));
        }
    }
}
