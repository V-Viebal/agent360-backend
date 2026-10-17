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
    private static readonly HashSet<string> ReservedLiteralSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "models", "skills", "approvals", "workspace", "providers", "analytics", "iam", "console", "browser", "chats", "health"
    };

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("agents")
            .WithTags(EndpointTags.Agents)
            .MapToApiVersion(ApiVersion.Default);

        // ==========================================
        // 1. Static Literal Routes (Priority 1)
        // ==========================================

        // 1a. List available LLM providers and models from catalog
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

        // 1b. Global / Agent-scoped Skills
        group.MapGet("skills", async ([FromQuery] string? agentId, IQwenPawClient client, CancellationToken ct) =>
        {
            var skills = await client.GetSkillsAsync(agentId, ct);
            return TypedResults.Ok(skills);
        })
        .WithName("ListSkills")
        .WithSummary("List all agent skills from infra (optionally scoped to specific bot)")
        .AllowAnonymous();

        // 1c. Approvals
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

        // 1d. Workspace Tree & File Content
        group.MapGet("workspace/tree", async ([FromQuery] string? agentId, IQwenPawClient client, CancellationToken ct) =>
        {
            var tree = await client.GetWorkspaceTreeAsync(agentId, ct);
            return TypedResults.Ok(tree);
        })
        .WithName("GetWorkspaceTree")
        .WithSummary("Get workspace file tree (optionally scoped to agent)")
        .AllowAnonymous();

        group.MapGet("workspace/file-content", async ([FromQuery] string path, [FromQuery] string? agentId, IQwenPawClient client, CancellationToken ct) =>
        {
            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            var content = await client.GetFileContentAsync(path, agentId, ct);
            return content is null ? Results.NotFound() : TypedResults.Ok(new { path, content });
        })
        .WithName("GetFileContent")
        .WithSummary("Read workspace file content with ACL enforcement (optionally scoped to agent)")
        .AllowAnonymous();

        // 1e. Global Chat History
        group.MapGet("chats/{chatId}", async (string chatId, [FromQuery] string? agentId, IQwenPawClient client, CancellationToken ct) =>
        {
            try
            {
                var historyRaw = await client.GetChatHistoryRawAsync(chatId, agentId, ct);
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

        // 1f. Console Chat Stream & Stop (mapped to both /agents/console/chat and root /console/chat for resilience)
        routeBuilder.MapPost("console/chat", async (
            [FromBody] ConsoleChatSendRequestDto request,
            HttpContext httpContext,
            IQwenPawClient client,
            SessionQueueManager queueManager,
            ILogger<AgentFeaturesEndpoints> logger,
            CancellationToken ct) =>
        {
            logger.LogInformation("Root console chat request for agent '{AgentId}', session '{SessionId}'", request.AgentId, request.SessionId);
            var effectiveSessionId = !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : $"session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            queueManager.SetSessionTurnActive(effectiveSessionId, true);
            try
            {
                using var response = await client.StreamConsoleChatAsync(request, ct);
                httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
                httpContext.Response.StatusCode = (int)response.StatusCode;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await SseRelay.CopyAsync(stream, httpContext.Response, TimeSpan.FromSeconds(15), ct);
            }
            finally
            {
                queueManager.SetSessionTurnActive(effectiveSessionId, false);
            }
        })
        .MapToApiVersion(ApiVersion.Default)
        .AllowAnonymous();

        group.MapPost("console/chat", async (
            [FromBody] ConsoleChatSendRequestDto request,
            HttpContext httpContext,
            IQwenPawClient client,
            SessionQueueManager queueManager,
            ILogger<AgentFeaturesEndpoints> logger,
            CancellationToken ct) =>
        {
            logger.LogInformation("Received console chat request for agent '{AgentId}', session '{SessionId}', message length: {Len}", request.AgentId, request.SessionId, request.Message?.Length ?? 0);
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

            var effectiveSessionId = !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : $"session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            queueManager.SetSessionTurnActive(effectiveSessionId, true);
            try
            {
                using var response = await client.StreamConsoleChatAsync(request, ct);
                httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
                httpContext.Response.StatusCode = (int)response.StatusCode;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await SseRelay.CopyAsync(stream, httpContext.Response, TimeSpan.FromSeconds(15), ct);
            }
            finally
            {
                queueManager.SetSessionTurnActive(effectiveSessionId, false);
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

        // 1g. Analytics & Audits
        group.MapGet("analytics/token-usage", async (IQwenPawClient client, CancellationToken ct) =>
        {
            var metrics = await client.GetTokenUsageAsync(ct);
            return metrics is null ? Results.NotFound() : TypedResults.Ok(metrics);
        })
        .WithName("GetTokenUsageAnalytics")
        .WithSummary("Aggregate token consumption and cache hit metrics")
        .AllowAnonymous();

        group.MapGet("providers/{providerId}/credentials/audit", (string providerId) =>
        {
            var auditResults = InfisicalCredentialAuditService.AuditProviderCredentials(providerId);
            return TypedResults.Ok(auditResults);
        })
        .WithName("AuditProviderCredentials")
        .WithSummary("Audit provider credentials against Infisical policy without exposing secrets")
        .AllowAnonymous();

        group.MapPost("iam/revoke", ([FromBody] RevokeIdentityCellRequest request) =>
        {
            IamRevocationGuard.MarkIdentityCellRevoked(request.IdentityCellId, request.Reason);
            return TypedResults.Ok(new { success = true, revoked = request.IdentityCellId });
        })
        .WithName("RevokeIdentityCell")
        .WithSummary("Revoke a Bot identity cell")
        .AllowAnonymous();

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

        // ==========================================
        // 2. Dynamic Agent-Scoped Sub-routes
        // ==========================================

        group.MapGet("{agentId}/chats", async (string agentId, IQwenPawClient client, CancellationToken ct) =>
        {
            try
            {
                var chatsJson = await client.GetAgentChatsRawAsync(agentId, ct);
                return Results.Content(chatsJson, "application/json");
            }
            catch (Exception)
            {
                var allChats = await client.GetChatsAsync(ct);
                return TypedResults.Ok(allChats);
            }
        })
        .WithName("GetAgentChats")
        .WithSummary("List all chat sessions for a specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/chats/{chatId}", async (string agentId, string chatId, IQwenPawClient client, CancellationToken ct) =>
        {
            try
            {
                var historyRaw = await client.GetChatHistoryRawAsync(chatId, agentId, ct);
                return Results.Content(historyRaw, "application/json");
            }
            catch (HttpRequestException)
            {
                return Results.NotFound(new { message = $"Chat {chatId} for agent {agentId} not found on infra" });
            }
        })
        .WithName("GetAgentChatHistory")
        .WithSummary("Get chat session messages for specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/skills", async (string agentId, IQwenPawClient client, CancellationToken ct) =>
            await RuntimeResultAsync(async () => TypedResults.Ok(await client.GetSkillsAsync(agentId, ct))))
        .WithName("ListAgentSkills")
        .WithSummary("List skills belonging to a specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/workspace/tree", async (string agentId, IQwenPawClient client, CancellationToken ct) =>
            await RuntimeResultAsync(async () => TypedResults.Ok(await client.GetWorkspaceTreeAsync(agentId, ct))))
        .WithName("GetAgentWorkspaceTree")
        .WithSummary("Get workspace file tree for specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/workspace/file-content", async (string agentId, [FromQuery] string path, IQwenPawClient client, CancellationToken ct) =>
        {
            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            return await RuntimeResultAsync(async () =>
            {
                var content = await client.GetFileContentAsync(path, agentId, ct);
                return content is null ? Results.NotFound() : TypedResults.Ok(new { path, content });
            });
        })
        .WithName("GetAgentFileContent")
        .WithSummary("Read workspace file content for specific agent")
        .AllowAnonymous();

        group.MapPut("{agentId}/active-model", async (string agentId, [FromBody] SetActiveModelRequestDto request, IQwenPawClient client, CancellationToken ct) =>
        {
            var res = await client.SetAgentActiveModelAsync(agentId, request.ProviderId, request.Model, ct);
            return res is null ? Results.Problem("Failed to switch model") : TypedResults.Ok(res);
        })
        .WithName("SetAgentActiveModel")
        .WithSummary("Hot-reload active LLM model provider and model for agent")
        .AllowAnonymous();

        // 2e. Native Context Management & Compaction (/workspace/checkpoints/gc and /compact trigger)
        group.MapPost("compact-context", async (
            [FromBody] CompactContextRequestDto request,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            var res = await client.CompactContextAsync(request, ct);
            return TypedResults.Ok(res);
        })
        .WithName("CompactAgentContext")
        .WithSummary("Perform context compaction and garbage collection on agent runtime")
        .AllowAnonymous();

        group.MapPost("{agentId}/compact-context", async (
            string agentId,
            [FromBody] CompactContextRequestDto request,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            var enriched = request with { AgentId = agentId };
            var res = await client.CompactContextAsync(enriched, ct);
            return TypedResults.Ok(res);
        })
        .WithName("CompactSpecificAgentContext")
        .WithSummary("Perform context compaction on specific agent runtime")
        .AllowAnonymous();

        // 2f. Tool Call Receipts & Audit Trail
        group.MapGet("sessions/{sessionId}/tool-receipts", async (
            string sessionId,
            [FromQuery] string? agentId,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            var receipts = await client.GetToolCallReceiptsAsync(sessionId, agentId, ct);
            return TypedResults.Ok(receipts);
        })
        .WithName("GetToolCallReceipts")
        .WithSummary("Retrieve verified tool call receipts for a session")
        .AllowAnonymous();

        // 3. Root Agent Profile Detail Route (Non-shadowing with literal guard)
        group.MapGet("{agentId}", async (string agentId, IQwenPawClient client, CancellationToken ct) =>
        {
            if (ReservedLiteralSegments.Contains(agentId))
            {
                return Results.NotFound(new { message = $"Route '{agentId}' is not an agent profile." });
            }

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
    }

    /// <summary>Maps runtime transport/status failures to their real HTTP status instead of 500.</summary>
    private static async Task<IResult> RuntimeResultAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException ex)
        {
            var status = (int)(ex.StatusCode ?? System.Net.HttpStatusCode.BadGateway);
            return Results.Problem(ex.Message, statusCode: status is >= 400 and < 600 ? status : 502, title: "Agent runtime request failed");
        }
    }
}

public sealed record RevokeIdentityCellRequest(string IdentityCellId, string Reason);
