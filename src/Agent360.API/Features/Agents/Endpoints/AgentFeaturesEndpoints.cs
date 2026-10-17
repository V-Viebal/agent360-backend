using System.Text;
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

        
        // 2b. List all available models from QwenPaw/LLM360 catalog
        group.MapGet("models", async (IQwenPawClient client, CancellationToken ct) =>
        {
            try
            {
                var modelsJson = await client.GetModelsAsync(ct);
                return Results.Content(modelsJson, "application/json");
            }
            catch (Exception)
            {
                return Results.Ok(new[]
                {
                    new { id = "llm360coding", name = "LLM360 Coding", models = new[] {
                        new { id = "gpt-5.6-luna", name = "GPT-5.6 Luna" },
                        new { id = "grok-4.7", name = "Grok 4.7" },
                        new { id = "gpt-6-sol", name = "GPT-6 Sol" },
                        new { id = "grok-4.6", name = "Grok 4.6" },
                        new { id = "claude-opus-4.6-thinking", name = "Claude Opus 4.6" },
                        new { id = "gemini-3.7-flash-tiered", name = "Gemini 3.7 Flash" }
                    }}
                });
            }
        })
        .WithName("ListModels")
        .WithSummary("List available LLM providers and models from infra")
        .AllowAnonymous();

        // 3. Chat History
        group.MapGet("chats/{chatId}", async (string chatId, IQwenPawClient client, CancellationToken ct) =>
        {
            try
            {
                var historyRaw = await client.GetChatHistoryRawAsync(chatId, ct);
                return Results.Content(historyRaw, "application/json");
            }
            catch (HttpRequestException)
            {
                return Results.NotFound(new { message = $"Chat {chatId} not found on infra" });
            }
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

        // 6. Console chat stream & stop with Per-session Queue & IAM360 Revocation Check
        group.MapPost("console/chat", async (
            [FromBody] ConsoleChatSendRequestDto request,
            HttpContext httpContext,
            IQwenPawClient client,
            SessionQueueManager queueManager,
            CancellationToken ct) =>
        {
            // IAM360 Revocation Gate Check
            var agentTarget = request.AgentId ?? "default";
            if (!IamRevocationGuard.IsExecutionAllowed(agentTarget, out var revocationReason))
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "IAM_REVOKED",
                    message = revocationReason
                }, ct);
                return;
            }

            // Per-session Turn Coordination
            queueManager.SetSessionTurnActive(request.SessionId, true);
            try
            {
                var response = await client.StreamConsoleChatAsync(request, ct);
                httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
                httpContext.Response.StatusCode = (int)response.StatusCode;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await stream.CopyToAsync(httpContext.Response.Body, ct);
            }
            finally
            {
                queueManager.SetSessionTurnActive(request.SessionId, false);
            }
        })
        .WithName("ConsoleChatStream")
        .WithSummary("SSE stream chat to agent with queue and IAM guard")
        .AllowAnonymous();

        group.MapPost("console/chat/stop", async ([FromBody] ConsoleChatStopRequestDto request, IQwenPawClient client, SessionQueueManager queueManager, CancellationToken ct) =>
        {
            queueManager.SetSessionTurnActive(request.SessionId, false);
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

        // 9. Token Usage Analytics
        group.MapGet("analytics/token-usage", async (IQwenPawClient client, CancellationToken ct) =>
        {
            var metrics = await client.GetTokenUsageAsync(ct);
            return metrics is null ? Results.NotFound() : TypedResults.Ok(metrics);
        })
        .WithName("GetTokenUsageAnalytics")
        .WithSummary("Aggregate token consumption and cache hit metrics")
        .AllowAnonymous();

        // 10. IAM360 Revocation Trigger (Simulation / Webhook)
        group.MapPost("iam/revoke", ([FromBody] RevokeIdentityCellRequest request) =>
        {
            IamRevocationGuard.MarkIdentityCellRevoked(request.IdentityCellId, request.Reason);
            return TypedResults.Ok(new { success = true, revoked = request.IdentityCellId });
        })
        .WithName("RevokeIdentityCell")
        .WithSummary("Revoke a Bot identity cell")
        .AllowAnonymous();

        // 11. Secure Upload Gateway (with Virtual Path Masking)
        group.MapPost("console/upload", async (
            IFormFile file,
            IWebHostEnvironment env,
            CancellationToken ct) =>
        {
            var (success, error, uploadResponse) = await SecureUploadStorageService.StoreFileAsync(
                file,
                env.ContentRootPath,
                ct);

            if (!success)
            {
                return Results.BadRequest(new { error });
            }

            return TypedResults.Ok(uploadResponse);
        })
        .WithName("SecureConsoleUpload")
        .WithSummary("Upload file attachment with virtual path masking and extension security check")
        .DisableAntiforgery()
        .AllowAnonymous();

        // 12. Remote Browser CDP Live Stream
        group.MapGet("browser/stream", async (
            [FromQuery] string? targetId,
            [FromQuery] int fps,
            HttpContext context,
            IBrowserCdpService cdpService,
            CancellationToken ct) =>
        {
            context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "close";

            var effectiveFps = Math.Clamp(fps <= 0 ? 5 : fps, 1, 15);
            var delayMs = 1000 / effectiveFps;
            var responseStream = context.Response.Body;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var imageBytes = await cdpService.CaptureScreenshotAsync(targetId, ct);

                    var headerText = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {imageBytes.Length}\r\n\r\n";
                    var headerBytes = Encoding.ASCII.GetBytes(headerText);

                    await responseStream.WriteAsync(headerBytes, ct);
                    await responseStream.WriteAsync(imageBytes, ct);
                    await responseStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);
                    await responseStream.FlushAsync(ct);

                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(500, ct);
                }
            }
        })
        .WithName("GetBrowserCdpStream")
        .WithSummary("Realtime MJPEG live stream of Chromium viewport for visual inspection")
        .AllowAnonymous();
    }
}

public sealed record RevokeIdentityCellRequest(string IdentityCellId, string Reason);
