using Agent360.API.Features.Billing;
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
        group.MapMethods("models", ["GET", "HEAD"], async ([FromQuery] string? view, HttpContext http, IQwenPawClient client, CancellationToken ct) =>
        {
            try
            {
                // HEAD is used as a cheap reachability/latency probe by the workspace.
                if (HttpMethods.IsHead(http.Request.Method))
                {
                    return Results.Ok();
                }
                // ~820 KB raw provider catalog; ?view=compact returns only what the model picker needs (~a few KB).
                if (string.Equals(view, "compact", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Content(await client.GetModelsCompactAsync(ct), "application/json");
                }
                var modelsJson = await client.GetModelsAsync(ct);
                return Results.Content(Agent360.API.Features.Agents.Security.SecretRedaction.RedactJson(modelsJson), "application/json");
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
        group.MapGet("workspace/tree", async (
            [FromQuery] string? agentId,
            [FromQuery] string? category,
            [FromQuery] string? ext,
            [FromQuery] string? search,
            [FromQuery] bool? includeSystem,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            var tree = await client.GetWorkspaceTreeAsync(agentId, ct, category, ext, search, includeSystem ?? false);
            return TypedResults.Ok(tree);
        })
        .WithName("GetWorkspaceTree")
        .WithSummary("Get workspace file tree and library artifacts (optionally scoped to agent, category, and format)")
        .AllowAnonymous();

        group.MapGet("workspace/file-download", async (
            [FromQuery] string path,
            [FromQuery] string? agentId,
            [FromQuery] bool? inline,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            using var resp = await client.DownloadWorkspaceFileAsync(path, agentId, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return Results.NotFound(new { message = $"File '{path}' not found" });
            }

            var ext = WorkspacePathAcl.ExtractExtension(path);
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? WorkspacePathAcl.GetMimeType(ext);
            var fileName = Path.GetFileName(path.Replace('\\', '/'));
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            if (inline == true)
            {
                httpContext.Response.Headers.ContentDisposition = $"inline; filename=\"{fileName.Replace("\"", "")}\"";
                return Results.Bytes(bytes, contentType);
            }

            return Results.File(bytes, contentType, fileName);
        })
        .WithName("DownloadWorkspaceFile")
        .WithSummary("Stream or download a workspace file with ACL enforcement")
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
        group.MapGet("chats/{chatId}", async (string chatId, [FromQuery] string? agentId, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            var roomStore = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>() ?? new Agent360.API.Features.Agents.SubAgents.SessionRoomStore();
            try
            {
                var historyRaw = await client.GetChatHistoryRawAsync(chatId, agentId, ct);
                return Results.Content(EnrichChatHistoryWithRoom(historyRaw, agentId, chatId, roomStore), "application/json");
            }
            catch (HttpRequestException)
            {
                var room = roomStore.GetByChat(agentId ?? "mons", chatId);
                if (room is not null)
                {
                    return Results.Content(EnrichChatHistoryWithRoom("{\"messages\":[],\"status\":\"idle\"}", agentId, chatId, roomStore), "application/json");
                }
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
            var agentTarget = request.AgentId ?? "default";
            var (accessAllowed, accessReason) = await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.CheckAccessAsync(
                agentTarget, httpContext, httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                httpContext.RequestServices.GetRequiredService<IConfiguration>(), ct);
            if (!accessAllowed)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "AGENT_ACCESS_DENIED",
                    message = accessReason
                }, ct);
                return;
            }
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

            var currentUser = httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>();
            var walletService = httpContext.RequestServices.GetRequiredService<WalletService>();
            var tenantId = currentUser.ExternalUserId ?? "default";

            var (walletOk, currentTokens, balanceReason) = await walletService.AssertSufficientBalanceAsync(tenantId, 100, ct);
            if (!walletOk)
            {
                httpContext.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "INSUFFICIENT_TOKENS",
                    message = balanceReason,
                    balance = currentTokens
                }, ct);
                return;
            }

            var effectiveSessionId = !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : $"session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            string qwenPawSessionId = effectiveSessionId;
            string? detectedChatChannel = request.Channel;
            try
            {
                var chatsRaw = await client.GetAgentChatsRawAsync(agentTarget, ct);
                if (!string.IsNullOrWhiteSpace(chatsRaw))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(chatsRaw);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                            var ch = el.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
                            if ((string.Equals(id, effectiveSessionId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(sid, effectiveSessionId, StringComparison.OrdinalIgnoreCase)))
                            {
                                if (!string.IsNullOrWhiteSpace(sid)) qwenPawSessionId = sid;
                                if (!string.IsNullOrWhiteSpace(ch)) detectedChatChannel = ch;
                                break;
                            }
                        }
                    }
                }
            }
            catch {}

            var sessionLock = queueManager.GetOrCreateSessionLock(qwenPawSessionId);
            await sessionLock.WaitAsync(ct);
            bool backgrounded = false;
            try
            {
                queueManager.SetSessionTurnActive(effectiveSessionId, true, agentTarget);
                if (!string.Equals(qwenPawSessionId, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    queueManager.SetSessionTurnActive(qwenPawSessionId, true, agentTarget);
                }

                Agent360.API.Features.Agents.Analytics.ChannelResponseStore.Record(
                    agentTarget, effectiveSessionId, request.Channel ?? "console", "user", request.Message ?? "", "submitted");

                var effectiveRequest = request with
                {
                    SessionId = qwenPawSessionId,
                    Channel = detectedChatChannel ?? request.Channel ?? "console"
                };

                backgrounded = await ExecuteConsoleChatStreamWithRoomSupportAsync(
                    effectiveRequest, agentTarget, effectiveSessionId, qwenPawSessionId, httpContext, client, queueManager, sessionLock, ct);
                _ = walletService.DeductTokensAsync(tenantId, 250, "INFERENCE_USAGE", $"AI Agent inference turn for '{agentTarget}'", effectiveSessionId);

                if (!backgrounded)
                {
                    client.InvalidateChatList(agentTarget);
                    Agent360.API.Features.Agents.Analytics.ChannelResponseStore.Record(
                        agentTarget, effectiveSessionId, request.Channel ?? "console", "assistant", "", "completed");
                }
            }
            finally
            {
                if (!backgrounded)
                {
                    queueManager.SetSessionTurnActive(effectiveSessionId, false, agentTarget);
                    if (!string.Equals(qwenPawSessionId, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        queueManager.SetSessionTurnActive(qwenPawSessionId, false, agentTarget);
                    }
                    sessionLock.Release();
                }
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
            var (accessAllowed, accessReason) = await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.CheckAccessAsync(
                agentTarget, httpContext, httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                httpContext.RequestServices.GetRequiredService<IConfiguration>(), ct);
            if (!accessAllowed)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "AGENT_ACCESS_DENIED",
                    message = accessReason
                }, ct);
                return;
            }
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

            var currentUser = httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>();
            var walletService = httpContext.RequestServices.GetRequiredService<WalletService>();
            var tenantId = currentUser.ExternalUserId ?? "default";

            var (walletOk, currentTokens, balanceReason) = await walletService.AssertSufficientBalanceAsync(tenantId, 100, ct);
            if (!walletOk)
            {
                httpContext.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "INSUFFICIENT_TOKENS",
                    message = balanceReason,
                    balance = currentTokens
                }, ct);
                return;
            }

            var effectiveSessionId = !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : $"session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            string qwenPawSessionId = effectiveSessionId;
            string? detectedChatChannel = request.Channel;
            try
            {
                var chatsRaw = await client.GetAgentChatsRawAsync(agentTarget, ct);
                if (!string.IsNullOrWhiteSpace(chatsRaw))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(chatsRaw);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                            var ch = el.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
                            if ((string.Equals(id, effectiveSessionId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(sid, effectiveSessionId, StringComparison.OrdinalIgnoreCase)))
                            {
                                if (!string.IsNullOrWhiteSpace(sid)) qwenPawSessionId = sid;
                                if (!string.IsNullOrWhiteSpace(ch)) detectedChatChannel = ch;
                                break;
                            }
                        }
                    }
                }
            }
            catch {}

            var sessionLock = queueManager.GetOrCreateSessionLock(qwenPawSessionId);
            await sessionLock.WaitAsync(ct);
            bool backgrounded = false;
            try
            {
                queueManager.SetSessionTurnActive(effectiveSessionId, true, agentTarget);
                if (!string.Equals(qwenPawSessionId, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    queueManager.SetSessionTurnActive(qwenPawSessionId, true, agentTarget);
                }

                Agent360.API.Features.Agents.Analytics.ChannelResponseStore.Record(
                    agentTarget, effectiveSessionId, request.Channel ?? "console", "user", request.Message ?? "", "submitted");

                var effectiveRequest = request with
                {
                    SessionId = qwenPawSessionId,
                    Channel = detectedChatChannel ?? request.Channel ?? "console"
                };

                backgrounded = await ExecuteConsoleChatStreamWithRoomSupportAsync(
                    effectiveRequest, agentTarget, effectiveSessionId, qwenPawSessionId, httpContext, client, queueManager, sessionLock, ct);
                _ = walletService.DeductTokensAsync(tenantId, 250, "INFERENCE_USAGE", $"AI Agent inference turn for '{agentTarget}'", effectiveSessionId);

                if (!backgrounded)
                {
                    client.InvalidateChatList(agentTarget);
                    Agent360.API.Features.Agents.Analytics.ChannelResponseStore.Record(
                        agentTarget, effectiveSessionId, request.Channel ?? "console", "assistant", "", "completed");
                }
            }
            finally
            {
                if (!backgrounded)
                {
                    queueManager.SetSessionTurnActive(effectiveSessionId, false, agentTarget);
                    if (!string.Equals(qwenPawSessionId, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        queueManager.SetSessionTurnActive(qwenPawSessionId, false, agentTarget);
                    }
                    sessionLock.Release();
                }
            }
        })
        .WithName("ConsoleChatStream")
        .WithSummary("SSE stream chat to agent with queue and IAM guard")
        .AllowAnonymous();

        group.MapPost("console/chat/stop", async ([FromBody] ConsoleChatStopRequestDto request, IQwenPawClient client, SessionQueueManager queueManager, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request?.SessionId))
            {
                return Results.Problem("session_id is required.", statusCode: StatusCodes.Status400BadRequest, title: "Bad Request");
            }
            queueManager.SetSessionTurnActive(request.SessionId, false, request.AgentId);
            var ok = await client.StopChatAsync(request, ct);
            return TypedResults.Ok(new { stopped = ok });
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
            if (string.IsNullOrWhiteSpace(request?.IdentityCellId))
            {
                return Results.Problem("identityCellId is required.", statusCode: StatusCodes.Status400BadRequest, title: "Bad Request");
            }
            IamRevocationGuard.MarkIdentityCellRevoked(request.IdentityCellId, request.Reason ?? "revoked");
            return TypedResults.Ok(new { success = true, revoked = request.IdentityCellId });
        })
        .WithName("RevokeIdentityCell")
        .WithSummary("Revoke a Bot identity cell")
        .AllowAnonymous();

        group.MapPost("console/upload", async (
            IFormFile file,
            IWebHostEnvironment env,
            Agent360.API.Features.Agents.Security.SecureUploadStorageService storageService,
            CancellationToken ct) =>
        {
            var (success, error, uploadResponse) = await storageService.StoreFileAsync(
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

        // Legacy unscoped core-browser stream: removed (it streamed one shared browser to any caller).
        group.MapGet("browser/stream", () => Results.Problem(
            detail: "Use /agents/{agentId}/browser/image.jpg or the agent-scoped /agents/{agentId}/browser/stream websocket.",
            statusCode: StatusCodes.Status410Gone, title: "Gone"))
        .WithName("GetBrowserCdpStream")
        .ExcludeFromDescription()
        .AllowAnonymous();

        // ==========================================
        // 2. Dynamic Agent-Scoped Sub-routes
        // ==========================================

        group.MapGet("{agentId}/chats", async (string agentId, HttpContext httpContext, IQwenPawClient client, SessionQueueManager queueManager, CancellationToken ct) =>
        {
            try
            {
                var chatsJson = await client.GetAgentChatsRawAsync(agentId, ct);
                if (string.IsNullOrWhiteSpace(chatsJson))
                {
                    return TypedResults.Ok(Array.Empty<AgentSessionResponse>());
                }

                using var doc = System.Text.Json.JsonDocument.Parse(chatsJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return Results.Content(chatsJson, "application/json");
                }

                var list = new List<System.Text.Json.Nodes.JsonObject>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(el.GetRawText()) as System.Text.Json.Nodes.JsonObject;
                    if (node is not null)
                    {
                        var id = node["id"]?.ToString() ?? "";
                        var sid = node["session_id"]?.ToString() ?? "";
                        httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>()?.BindSessionAlias(agentId, id, sid);
                        var rawStatus = node["status"]?.ToString() ?? "idle";
                        var isRunning = string.Equals(rawStatus, "running", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(rawStatus, "in_progress", StringComparison.OrdinalIgnoreCase)
                            || queueManager.IsSessionTurnActive(id)
                            || (!string.IsNullOrWhiteSpace(sid) && queueManager.IsSessionTurnActive(sid));

                        var isUnread = Agent360.API.Features.Agents.Analytics.ChannelResponseStore.IsUnread(id)
                            || (!string.IsNullOrWhiteSpace(sid) && Agent360.API.Features.Agents.Analytics.ChannelResponseStore.IsUnread(sid));

                        node["is_running"] = isRunning;
                        node["isRunning"] = isRunning;
                        node["unread"] = isUnread;
                        if (isRunning) node["status"] = "running";
                        list.Add(node);
                    }
                }

                return Results.Ok(list);
            }
            catch (Exception)
            {
                // Never leak other agents' chats when an agent has no chats or is disabled/unhosted
                return TypedResults.Ok(Array.Empty<AgentSessionResponse>());
            }
        })
        .WithName("GetAgentChats")
        .WithSummary("List all chat sessions for a specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/chats/{chatId}", async (string agentId, string chatId, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            var roomStore = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>() ?? new Agent360.API.Features.Agents.SubAgents.SessionRoomStore();
            try
            {
                var historyRaw = await client.GetChatHistoryRawAsync(chatId, agentId, ct);
                return Results.Content(EnrichChatHistoryWithRoom(historyRaw, agentId, chatId, roomStore), "application/json");
            }
            catch (HttpRequestException)
            {
                var room = roomStore.GetByChat(agentId, chatId);
                if (room is not null)
                {
                    return Results.Content(EnrichChatHistoryWithRoom("{\"messages\":[],\"status\":\"idle\"}", agentId, chatId, roomStore), "application/json");
                }
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

        group.MapGet("{agentId}/workspace/tree", async (
            string agentId,
            [FromQuery] string? category,
            [FromQuery] string? ext,
            [FromQuery] string? search,
            [FromQuery] bool? includeSystem,
            IQwenPawClient client,
            CancellationToken ct) =>
            await RuntimeResultAsync(async () => TypedResults.Ok(await client.GetWorkspaceTreeAsync(agentId, ct, category, ext, search, includeSystem ?? false))))
        .WithName("GetAgentWorkspaceTree")
        .WithSummary("Get workspace library artifacts and file tree for specific agent with category/format filters")
        .AllowAnonymous();

        group.MapGet("{agentId}/workspace/file-download", async (
            string agentId,
            [FromQuery] string path,
            [FromQuery] bool? inline,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
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
                using var resp = await client.DownloadWorkspaceFileAsync(path, agentId, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return Results.NotFound(new { message = $"File '{path}' not found" });
                }

                var ext = WorkspacePathAcl.ExtractExtension(path);
                var contentType = resp.Content.Headers.ContentType?.ToString() ?? WorkspacePathAcl.GetMimeType(ext);
                var fileName = Path.GetFileName(path.Replace('\\', '/'));
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

                if (inline == true)
                {
                    httpContext.Response.Headers.ContentDisposition = $"inline; filename=\"{fileName.Replace("\"", "")}\"";
                    return Results.Bytes(bytes, contentType);
                }

                return Results.File(bytes, contentType, fileName);
            });
        })
        .WithName("DownloadAgentWorkspaceFile")
        .WithSummary("Download or preview a workspace artifact file for specific agent")
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

        group.MapPut("{agentId}/active-model", async (
            string agentId,
            [FromBody] SetActiveModelRequestDto request,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            var (accessAllowed, accessReason) = await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.CheckAccessAsync(
                agentId, httpContext, httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                httpContext.RequestServices.GetRequiredService<IConfiguration>(), ct);
            if (!accessAllowed)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Results.Problem(accessReason ?? "Agent access denied", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
            }

            var res = await client.SetAgentActiveModelAsync(agentId, request.ProviderId, request.Model, ct);
            return res is null ? Results.Problem("Failed to switch model") : TypedResults.Ok(res);
        })
        .WithName("SetAgentActiveModel")
        .WithSummary("Hot-reload active LLM model provider and model for agent")
        .AllowAnonymous();

        group.MapPatch("{agentId}/toggle", async (
            string agentId,
            [FromBody] ToggleAgentRequest request,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            var (accessAllowed, accessReason) = await Agent360.API.Features.Agents.Security.AgentOwnershipGuard.CheckAccessAsync(
                agentId, httpContext, httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                httpContext.RequestServices.GetRequiredService<IConfiguration>(), ct);
            if (!accessAllowed)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Results.Problem(accessReason ?? "Agent access denied", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
            }

            var res = await client.ToggleAgentAsync(agentId, request.Enabled, ct);
            return res is null ? Results.Problem("Failed to toggle agent enabled state") : TypedResults.Ok(res);
        })
        .WithName("ToggleAgentEnabled")
        .WithSummary("Enable or disable agent profile on runtime")
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
                return Results.Content(Agent360.API.Features.Agents.Security.SecretRedaction.RedactJson(profile), "application/json");
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

    private static async Task<bool> ExecuteConsoleChatStreamWithRoomSupportAsync(
        ConsoleChatSendRequestDto effectiveRequest,
        string agentTarget,
        string effectiveSessionId,
        string qwenPawSessionId,
        HttpContext httpContext,
        IQwenPawClient client,
        SessionQueueManager queueManager,
        SemaphoreSlim sessionLock,
        CancellationToken ct)
    {
        var upstreamCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var rawUserText = effectiveRequest.Message;
        if (string.IsNullOrWhiteSpace(rawUserText) && effectiveRequest.Input is { Count: > 0 })
        {
            rawUserText = effectiveRequest.Input[0].Content?.FirstOrDefault()?.Text ?? "";
        }

        var mentionedCollaborators = Agent360.API.Features.Agents.SubAgents.SessionRoomOrchestrator.DetectMentionedCollaborators(rawUserText, agentTarget);
        var roomOrchestrator = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomOrchestrator>();

        if (mentionedCollaborators.Count == 0 && roomOrchestrator is not null)
        {
            // Non-collaborator turn: dispatch room provisioning/title sync in background so QwenPaw receives the prompt with 0ms delay
            _ = Task.Run(async () =>
            {
                try
                {
                    var bgRoom = await roomOrchestrator.EnsureRoomAsync(agentTarget, effectiveSessionId, null, qwenPawSessionId, CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(bgRoom.RoomTitle) && !string.IsNullOrWhiteSpace(rawUserText))
                    {
                        await roomOrchestrator.SyncRoomTitleAsync(agentTarget, effectiveSessionId, rawUserText, CancellationToken.None);
                    }
                }
                catch {}
            });
        }
        else if (mentionedCollaborators.Count > 0 && roomOrchestrator is not null)
        {
            if (!ct.IsCancellationRequested)
            {
                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";
                httpContext.Response.Headers.CacheControl = "no-cache";
                try { await httpContext.Response.StartAsync(ct); } catch {}
            }

            var baseRoom = await roomOrchestrator.EnsureRoomAsync(
                agentTarget,
                effectiveSessionId,
                mentionedCollaborators.FirstOrDefault(),
                qwenPawSessionId,
                upstreamCts.Token,
                mentionedCollaborators);
            if (string.IsNullOrWhiteSpace(baseRoom.RoomTitle) && !string.IsNullOrWhiteSpace(rawUserText))
            {
                baseRoom = await roomOrchestrator.SyncRoomTitleAsync(agentTarget, effectiveSessionId, rawUserText, upstreamCts.Token) ?? baseRoom;
            }

            if (!ct.IsCancellationRequested)
            {
                var initRoomEvt = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @object = "room_event",
                    status = "collaborator_running",
                    collaborator_id = mentionedCollaborators.FirstOrDefault() ?? agentTarget,
                    room = baseRoom
                });
                try
                {
                    await httpContext.Response.WriteAsync($"data: {initRoomEvt}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch {}
            }

            var accumulatedContext = new System.Text.StringBuilder();
            Agent360.API.Features.Agents.SubAgents.SessionCollaborationRoom? latestRoom = null;

            foreach (var mentionedCollaborator in mentionedCollaborators)
            {
                var ensuredRoom = await roomOrchestrator.EnsureRoomAsync(agentTarget, effectiveSessionId, mentionedCollaborator, qwenPawSessionId, upstreamCts.Token);
                if (!ct.IsCancellationRequested)
                {
                    var roomInitEvt = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        @object = "room_event",
                        status = "collaborator_running",
                        collaborator_id = mentionedCollaborator,
                        room = ensuredRoom
                    });
                    try
                    {
                        await httpContext.Response.WriteAsync($"data: {roomInitEvt}\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    }
                    catch {}
                }

                var (completedRoom, collabTurn) = await roomOrchestrator.ExecuteCollaboratorTurnAsync(
                    agentTarget,
                    effectiveSessionId,
                    mentionedCollaborator,
                    rawUserText ?? "",
                    accumulatedContext.Length > 0 ? accumulatedContext.ToString() : null,
                    qwenPawSessionId,
                    upstreamCts.Token);

                latestRoom = completedRoom;
                accumulatedContext.AppendLine($"- @{mentionedCollaborator} ({collabTurn.AuthorName}): {collabTurn.Text}");

                if (!ct.IsCancellationRequested)
                {
                    var roomTurnEvt = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        @object = "room_turn",
                        status = completedRoom.Status,
                        collaborator_id = mentionedCollaborator,
                        room = completedRoom,
                        turn = collabTurn
                    });
                    try
                    {
                        await httpContext.Response.WriteAsync($"data: {roomTurnEvt}\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    }
                    catch {}
                }
            }

            if (latestRoom is not null && !ct.IsCancellationRequested)
            {
                var hostSynthEvt = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @object = "room_event",
                    status = "synthesizing",
                    collaborator_id = agentTarget,
                    room = latestRoom
                });
                try
                {
                    await httpContext.Response.WriteAsync($"data: {hostSynthEvt}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch {}
            }

            var synthesizedInput = $"{rawUserText}\n\n[SHARED SESSION ROOM CONTEXT]\nCác chuyên viên trong phiên đã lần lượt phản hồi:\n{accumulatedContext}\nHãy dựa trên ngữ cảnh chung và các phản hồi trên để tổng hợp kết quả cuối cùng cho người dùng.";
            effectiveRequest = effectiveRequest with
            {
                Message = synthesizedInput,
                Input =
                [
                    new ConsoleChatInputItem(
                        "user",
                        "message",
                        [new ConsoleChatContentItem("text", synthesizedInput)])
                ]
            };
        }
        HttpResponseMessage? response = null;
        Stream? stream = null;
        try
        {
            response = await client.StreamConsoleChatAsync(effectiveRequest, upstreamCts.Token);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
                httpContext.Response.StatusCode = (int)response.StatusCode;
            }

            stream = await response.Content.ReadAsStreamAsync(upstreamCts.Token);
            await SseRelay.CopyAsync(stream, httpContext.Response, TimeSpan.FromSeconds(5), ct, sendInitialPing: true);
            await stream.DisposeAsync();
            response.Dispose();
            upstreamCts.Dispose();
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !upstreamCts.IsCancellationRequested && response is not null && stream is not null)
        {
            // Browser reloaded or disconnected while QwenPaw is still executing the turn.
            // Keep draining the upstream stream in background so QwenPaw finishes cleanly and
            // SessionQueueManager keeps is_running=true until the turn actually ends (allowing /live reconnect).
            var capturedResponse = response;
            var capturedStream = stream;
            var capturedCts = upstreamCts;
            var capturedChannel = effectiveRequest.Channel ?? "console";
            _ = Task.Run(async () =>
            {
                try
                {
                    var drainBuf = new byte[8192];
                    while (await capturedStream.ReadAsync(drainBuf, capturedCts.Token) > 0)
                    {
                    }
                }
                catch
                {
                }
                finally
                {
                    try { client.InvalidateChatList(agentTarget); } catch {}
                    try
                    {
                        Agent360.API.Features.Agents.Analytics.ChannelResponseStore.Record(
                            agentTarget, effectiveSessionId, capturedChannel, "assistant", "", "completed");
                    }
                    catch {}
                    queueManager.SetSessionTurnActive(effectiveSessionId, false, agentTarget);
                    if (!string.Equals(qwenPawSessionId, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        queueManager.SetSessionTurnActive(qwenPawSessionId, false, agentTarget);
                    }
                    try { sessionLock.Release(); } catch {}
                    try { await capturedStream.DisposeAsync(); } catch {}
                    try { capturedResponse.Dispose(); } catch {}
                    try { capturedCts.Dispose(); } catch {}
                }
            });
            return true;
        }
        catch
        {
            if (stream is not null) { try { await stream.DisposeAsync(); } catch {} }
            response?.Dispose();
            upstreamCts.Dispose();
            throw;
        }
    }

    private static string EnrichChatHistoryWithRoom(
        string historyRaw,
        string? agentId,
        string chatId,
        Agent360.API.Features.Agents.SubAgents.SessionRoomStore roomStore)
    {
        try
        {
            var room = roomStore.GetByChat(agentId ?? "mons", chatId);
            if (room is null) return historyRaw;

            var node = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(historyRaw) ? "{}" : historyRaw) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();

            node["collaboration_room"] = System.Text.Json.JsonSerializer.SerializeToNode(room);

            if (node["messages"] is not System.Text.Json.Nodes.JsonArray messagesArr)
            {
                messagesArr = new System.Text.Json.Nodes.JsonArray();
                node["messages"] = messagesArr;
            }

            // Strip internal [SHARED SESSION ROOM CONTEXT] synthesis block from persisted user messages so the UI only shows the user's clean message
            foreach (var msgNode in messagesArr.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if (!string.Equals(msgNode["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase)) continue;
                if (msgNode["content"] is System.Text.Json.Nodes.JsonValue strVal && strVal.TryGetValue<string>(out var rawStr) && !string.IsNullOrEmpty(rawStr))
                {
                    var idx = rawStr.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) idx = rawStr.IndexOf("[AGENTTEAMS COLLABORATION ROOM", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) msgNode["content"] = rawStr[..idx].TrimEnd();
                }
                else if (msgNode["content"] is System.Text.Json.Nodes.JsonArray contentArr)
                {
                    foreach (var part in contentArr.OfType<System.Text.Json.Nodes.JsonObject>())
                    {
                        if (part["text"] is System.Text.Json.Nodes.JsonValue tVal && tVal.TryGetValue<string>(out var partStr) && !string.IsNullOrEmpty(partStr))
                        {
                            var idx = partStr.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) idx = partStr.IndexOf("[AGENTTEAMS COLLABORATION ROOM", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0) part["text"] = partStr[..idx].TrimEnd();
                        }
                    }
                }
            }

            foreach (var turn in room.Turns.Where(t => t.Kind is "collaborator_reply" or "question" or "manager_guard"))
            {
                var alreadyPresent = messagesArr.Any(m => m?["id"]?.ToString() == turn.Id);
                if (alreadyPresent) continue;

                var msgObj = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = turn.Id,
                    ["role"] = "assistant",
                    ["type"] = "message",
                    ["status"] = "completed",
                    ["content"] = new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = turn.Text
                        }),
                    ["metadata"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["timestamp"] = turn.CreatedAt.ToString("O"),
                        ["sender_agent_id"] = turn.AuthorId,
                        ["sender_agent_name"] = turn.AuthorName,
                        ["author_role"] = turn.AuthorRole,
                        ["turn_kind"] = turn.Kind,
                        ["room_id"] = room.RoomId,
                        ["matrix_room_id"] = room.MatrixRoomId,
                        ["element_room_url"] = room.ElementRoomUrl
                    }
                };
                messagesArr.Add(msgObj);
            }

            return node.ToJsonString();
        }
        catch
        {
            return historyRaw;
        }
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
