using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Endpoints;

public sealed class AgentFeaturesEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("agents")
            .WithTags(EndpointTags.Agents)
            .MapToApiVersion(ApiVersion.Default);

        // 1. Get Agent Profile Detail
        group.MapGet("{agentId}", async (string agentId, IQwenPawClient client, CancellationToken ct) =>
        {
            try
            {
                var profile = await client.GetAgentProfileRawAsync(agentId, ct);
                return Results.Content(profile, "application/json");
            }
            catch (HttpRequestException)
            {
                return Results.NotFound(new { message = $"Agent profile {agentId} not found on infra" });
            }
        })
        .WithName("GetAgentProfile")
        .WithSummary("Get agent profile details")
        .AllowAnonymous();

        // 2. Set Active Model for Agent (Hot-reload override)
        group.MapPut("{agentId}/active-model", async (string agentId, [FromBody] SetActiveModelRequestDto request, IQwenPawClient client, CancellationToken ct) =>
        {
            var res = await client.SetAgentActiveModelAsync(agentId, request.ProviderId, request.Model, ct);
            return res is null ? Results.Problem("Failed to switch model") : TypedResults.Ok(res);
        })
        .WithName("SetAgentActiveModel")
        .WithSummary("Hot-reload active LLM model provider and model for agent")
        .AllowAnonymous();

        // 3. Chat History
        group.MapGet("chats/{chatId}", async (string chatId, IQwenPawClient client, CancellationToken ct) =>
        {
            var history = await client.GetChatHistoryAsync(chatId, ct);
            return history is null ? Results.NotFound() : TypedResults.Ok(history);
        })
        .WithName("GetChatHistory")
        .WithSummary("Get chat session messages")
        .AllowAnonymous();

        // 4. Skills
        group.MapGet("skills", async (IQwenPawClient client, CancellationToken ct) =>
        {
            var skills = await client.GetSkillsAsync(ct);
            return TypedResults.Ok(skills);
        })
        .WithName("ListSkills")
        .WithSummary("List all agent skills from infra")
        .AllowAnonymous();

        // 5. Approvals
        group.MapGet("approvals", async ([FromQuery] string? sessionId, IQwenPawClient client, CancellationToken ct) =>
        {
            var approvals = await client.GetApprovalsAsync(sessionId, ct);
            return TypedResults.Ok(approvals);
        })
        .WithName("ListApprovals")
        .WithSummary("List pending tool execution approvals")
        .AllowAnonymous();

        group.MapPost("approvals/approve", async ([FromBody] ApprovalActionRequestDto request, IQwenPawClient client, CancellationToken ct) =>
        {
            var res = await client.ApproveToolAsync(request, ct);
            return TypedResults.Ok(res);
        })
        .WithName("ApproveTool")
        .WithSummary("Approve pending tool call")
        .AllowAnonymous();

        group.MapPost("approvals/deny", async ([FromBody] ApprovalActionRequestDto request, IQwenPawClient client, CancellationToken ct) =>
        {
            var res = await client.DenyToolAsync(request, ct);
            return TypedResults.Ok(res);
        })
        .WithName("DenyTool")
        .WithSummary("Deny pending tool call")
        .AllowAnonymous();

        // 6. Console chat stream & stop
        group.MapPost("console/chat", async ([FromBody] ConsoleChatSendRequestDto request, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            var response = await client.StreamConsoleChatAsync(request, ct);
            httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
            httpContext.Response.StatusCode = (int)response.StatusCode;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(httpContext.Response.Body, ct);
        })
        .WithName("ConsoleChatStream")
        .WithSummary("SSE stream chat to agent")
        .AllowAnonymous();

        group.MapPost("console/chat/stop", async ([FromBody] ConsoleChatStopRequestDto request, IQwenPawClient client, CancellationToken ct) =>
        {
            var ok = await client.StopChatAsync(request, ct);
            return ok ? TypedResults.Ok(new { stopped = true }) : Results.Problem("Failed to stop chat execution or no active turn.");
        })
        .WithName("ConsoleChatStop")
        .WithSummary("Stop active agent turn")
        .AllowAnonymous();

        // 7. Workspace File Browser with Path ACL
        group.MapGet("workspace/tree", async (IQwenPawClient client, CancellationToken ct) =>
        {
            var tree = await client.GetWorkspaceTreeAsync(ct);
            return TypedResults.Ok(tree);
        })
        .WithName("GetWorkspaceTree")
        .WithSummary("Get workspace file tree")
        .AllowAnonymous();

        group.MapGet("workspace/file-content", async ([FromQuery] string path, IQwenPawClient client, CancellationToken ct) =>
        {
            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            var content = await client.GetFileContentAsync(path, ct);
            return content is null ? Results.NotFound() : TypedResults.Ok(new { path, content });
        })
        .WithName("GetFileContent")
        .WithSummary("Read workspace file content with ACL enforcement")
        .AllowAnonymous();
        // 8. Infisical Credential Audit Endpoint
        group.MapGet("providers/{providerId}/credentials/audit", (string providerId) =>
        {
            var auditResults = InfisicalCredentialAuditService.AuditProviderCredentials(providerId);
            return TypedResults.Ok(auditResults);
        })
        .WithName("AuditProviderCredentials")
        .WithSummary("Audit provider credentials against Infisical policy without exposing secrets")
        .AllowAnonymous();

    }
}
