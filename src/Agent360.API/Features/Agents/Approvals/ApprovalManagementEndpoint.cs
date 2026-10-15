using System.Net.Http.Json;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Approvals;

public sealed class ApprovalManagementEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List pending approvals
        routeBuilder.MapGet(
            "approvals",
            async Task<IResult> (
                [FromQuery] string? sessionId,
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var path = string.IsNullOrEmpty(sessionId)
                    ? "api/approval/list"
                    : $"api/approval/list?session_id={Uri.EscapeDataString(sessionId)}";

                var requestUri = new Uri(baseUri, path);

                var response = await httpClient.GetFromJsonAsync<ApprovalListApiResponse>(
                    requestUri,
                    cancellationToken);

                return TypedResults.Ok(response ?? new ApprovalListApiResponse(Array.Empty<ApprovalItemDto>(), 0));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListPendingApprovals")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all pending security/tool approvals for agents")
            .AllowAnonymous();

        // 2. Submit approval decision (Approve or Deny)
        routeBuilder.MapPost(
            "approvals/decision",
            async Task<IResult> (
                [FromBody] ApprovalDecisionRequest request,
                HttpClient httpClient,
                IOptions<QwenPawOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opt = options.Value;
                var baseUri = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                var action = request.Approve ? "approve" : "deny";
                var requestUri = new Uri(baseUri, $"api/approval/{action}");

                var payload = new
                {
                    request_id = request.RequestId,
                    session_id = request.SessionId,
                    reason = request.Reason,
                    scope = request.Scope ?? "exact"
                };

                var res = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);
                if (res.IsSuccessStatusCode)
                {
                    return TypedResults.Ok(new ApprovalDecisionResponse(
                        Success: true,
                        Message: request.Approve ? "Approval granted" : "Approval denied",
                        RequestId: request.RequestId,
                        Decision: request.Approve ? "approved" : "denied"));
                }

                return TypedResults.Problem($"Failed to resolve approval: {res.StatusCode}");
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SubmitApprovalDecision")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Approve or deny an agent tool execution request")
            .AllowAnonymous();
    }
}
