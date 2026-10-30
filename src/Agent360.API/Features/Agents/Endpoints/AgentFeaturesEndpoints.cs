using System.Security.Cryptography;
using Agent360.API.Identity;
using Agent360.API.Features.Agents.SubAgents;
using Microsoft.Extensions.Options;
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
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, ct) is { } denied) return denied;
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
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, ct) is { } denied) return denied;
            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            var targetAgentId = agentId ?? "mons";
            var resp = await client.DownloadWorkspaceFileAsync(path, targetAgentId, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var bareName = Path.GetFileName(path.Replace('\\', '/'));
                if (!string.IsNullOrWhiteSpace(bareName))
                {
                    foreach (var fallbackCandidate in new[] { $"generated-images/{bareName}", $"media/{bareName}", bareName })
                    {
                        if (string.Equals(fallbackCandidate, path, StringComparison.OrdinalIgnoreCase)) continue;
                        var nextResp = await client.DownloadWorkspaceFileAsync(fallbackCandidate, targetAgentId, ct);
                        if (nextResp.IsSuccessStatusCode)
                        {
                            resp.Dispose();
                            resp = nextResp;
                            break;
                        }
                        nextResp.Dispose();
                    }
                }
            }
            using (resp)
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

        group.MapGet("workspace/file-content", async ([FromQuery] string path, [FromQuery] string? agentId, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, ct) is { } denied) return denied;
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
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, ct) is { } denied) return denied;
            if (await ChatVisibilityGuard.EnforceChatAsync(agentId ?? "mons", chatId, httpContext, client, ct) is { } hidden) return hidden;
            var roomStore = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>() ?? new Agent360.API.Features.Agents.SubAgents.SessionRoomStore();
            try
            {
                var historyRaw = await client.GetChatHistoryRawAsync(chatId, agentId, ct);
                return Results.Content(EnrichChatHistoryWithRoom(historyRaw, agentId, chatId, roomStore), "application/json");
            }
            catch (HttpRequestException)
            {
                var room = !string.IsNullOrWhiteSpace(agentId) ? roomStore.GetByChat(agentId, chatId) : roomStore.GetByChatIdOnly(chatId);
                if (room is not null)
                {
                    return Results.Content(EnrichChatHistoryWithRoom("{\"messages\":[],\"status\":\"idle\"}", agentId, chatId, roomStore), "application/json");
                }
                if (chatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Content("{\"messages\":[],\"status\":\"idle\"}", "application/json");
                }
                return Results.NotFound(new { message = $"Chat {chatId} not found on infra" });
            }
        })
        .WithName("GetChatHistory")
        .WithSummary("Get chat session messages")
        .AllowAnonymous();

        // 1e-reaction. Submit message reaction (implicit evaluation & agent treatment)
        group.MapPost("{agentId}/chats/{chatId}/messages/{messageId}/reactions", async (
            string agentId,
            string chatId,
            string messageId,
            [FromBody] Agent360.API.Features.Agents.SubAgents.SubmitMessageReactionRequestDto request,
            HttpContext httpContext,
            IQwenPawClient client,
            SessionQueueManager queueManager,
            ILogger<AgentFeaturesEndpoints> logger,
            CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            var roomStore = httpContext.RequestServices.GetRequiredService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
            var currentUser = httpContext.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>();
            var userId = currentUser.ExternalUserId ?? "user";

            var room = await roomStore.RecordReactionAsync(
                agentId,
                chatId,
                messageId,
                request.Emoji,
                userId,
                request.Action ?? "add",
                ct);

            // If action is remove or trigger_agent_response is false, return updated room immediately
            var isAdd = !string.Equals(request.Action, "remove", StringComparison.OrdinalIgnoreCase);
            if (!isAdd || !request.TriggerAgentResponse)
            {
                return Results.Ok(new { success = true, action = request.Action ?? "add", room });
            }

            // Implicit Evaluation Turn: Build implicit prompt and stream directly to QwenPaw
            var targetAgent = !string.IsNullOrWhiteSpace(request.TargetAgentId) ? request.TargetAgentId : agentId;
            var implicitPrompt = Agent360.API.Features.Agents.SubAgents.AgentReactionClassifier.BuildImplicitReactionPrompt(
                request.Emoji, request.MessageSnippet, targetAgent);

            var effectiveSessionId = !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : chatId;

            var consoleReq = new ConsoleChatSendRequestDto(
                effectiveSessionId,
                implicitPrompt,
                targetAgent,
                null,
                [new ConsoleChatInputItem("user", "message", [new ConsoleChatContentItem("text", implicitPrompt)])],
                "console");

            var sessionLock = queueManager.GetOrCreateSessionLock(effectiveSessionId);
            await sessionLock.WaitAsync(ct);
            queueManager.SetSessionTurnActive(effectiveSessionId, true, targetAgent);

            try
            {
                var backgrounded = await ExecuteConsoleChatStreamWithRoomSupportAsync(
                    consoleReq,
                    targetAgent,
                    effectiveSessionId,
                    effectiveSessionId,
                    httpContext,
                    client,
                    queueManager,
                    sessionLock,
                    ct);

                return Results.Empty;
            }
            finally
            {
                queueManager.SetSessionTurnActive(effectiveSessionId, false, targetAgent);
                sessionLock.Release();
            }
        })
        .WithName("SubmitChatMessageReaction")
        .WithSummary("Submit reaction to an agent message and stream implicit response evaluation")
        .AllowAnonymous();

        // Summary of reactions for agent telemetry
        group.MapGet("{agentId}/reactions/summary", async (
            string agentId,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            var roomStore = httpContext.RequestServices.GetRequiredService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
            var summary = roomStore.GetReactionSummary(agentId);
            return Results.Ok(summary);
        })
        .WithName("GetAgentReactionSummary")
        .WithSummary("Get positive/negative/neutral reaction evaluation summary for agent")
        .AllowAnonymous();

        // 1e-pin. Pin or unpin a message within an agent chat session (persisted to SessionRoomStore & QwenPaw workspace)
        group.MapMethods("{agentId}/chats/{chatId}/messages/{messageId}/pin", ["POST", "PUT", "PATCH"], async (
            string agentId,
            string chatId,
            string messageId,
            [FromBody] Agent360.API.Features.Agents.SubAgents.PinMessageRequestDto? request,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var roomStore = httpContext.RequestServices.GetRequiredService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
            var currentUser = httpContext.RequestServices.GetService<Agent360.API.Identity.CurrentUser>();
            var userId = currentUser?.ExternalUserId ?? "user";

            var (room, isPinned, pinnedMessageIds) = await roomStore.RecordPinnedMessageAsync(
                agentId,
                chatId,
                messageId,
                request?.Pinned,
                request?.Action,
                request?.Role,
                request?.Snippet,
                userId,
                ct);

            // Sync agent's full pinned messages ledger down to QwenPaw workspace file (.agent360_pinned_messages.json)
            var allAgentPinned = roomStore.GetPinnedMessagesForAgent(agentId);
            _ = Task.Run(async () =>
            {
                try
                {
                    var ledgerJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        agent_id = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant(),
                        updated_at = DateTimeOffset.UtcNow.ToString("O"),
                        pinned_messages = allAgentPinned
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await client.SaveFileContentAsync(".agent360_pinned_messages.json", ledgerJson, agentId, CancellationToken.None);
                }
                catch { }
            });

            var multiplexerHub = httpContext.RequestServices.GetService<SessionMultiplexerHub>();
            if (multiplexerHub is not null)
            {
                var normAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
                _ = multiplexerHub.BroadcastAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "chat_message_pin_updated",
                    agent_id = normAgent,
                    chat_id = chatId,
                    message_id = messageId,
                    pinned = isPinned,
                    pinned_message_ids = pinnedMessageIds,
                    pinned_messages = room.PinnedMessages,
                    timestamp = DateTimeOffset.UtcNow.ToString("O")
                }));
            }

            return Results.Ok(new
            {
                success = true,
                agent_id = agentId,
                chat_id = chatId,
                message_id = messageId,
                pinned = isPinned,
                pinned_message_ids = pinnedMessageIds,
                pinned_messages = room.PinnedMessages
            });
        })
        .WithName("PinChatMessage")
        .WithSummary("Pin or unpin a message in an agent chat session and synchronize to QwenPaw workspace")
        .AllowAnonymous();

        // Get pinned messages for a specific chat session
        group.MapGet("{agentId}/chats/{chatId}/pinned-messages", async (
            string agentId,
            string chatId,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var roomStore = httpContext.RequestServices.GetRequiredService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
            var list = roomStore.GetPinnedMessagesForAgent(agentId, chatId);
            return Results.Ok(new
            {
                agent_id = agentId,
                chat_id = chatId,
                pinned_message_ids = list.Select(x => x.MessageId).ToArray(),
                pinned_messages = list
            });
        })
        .WithName("GetChatPinnedMessages")
        .WithSummary("Get pinned messages for a specific agent chat session")
        .AllowAnonymous();

        // Get all pinned messages across all sessions for an agent (hydrates from QwenPaw workspace if empty)
        group.MapGet("{agentId}/pinned-messages", async (
            string agentId,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var roomStore = httpContext.RequestServices.GetRequiredService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
            var list = roomStore.GetPinnedMessagesForAgent(agentId);
            if (list.Count == 0)
            {
                try
                {
                    var raw = await client.GetFileContentAsync(".agent360_pinned_messages.json", agentId, ct);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(raw);
                        if (doc.RootElement.TryGetProperty("pinned_messages", out var pmArr) && pmArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var hydrated = System.Text.Json.JsonSerializer.Deserialize<List<Agent360.API.Features.Agents.SubAgents.PinnedMessageRecord>>(pmArr.GetRawText());
                            if (hydrated is { Count: > 0 })
                            {
                                foreach (var item in hydrated)
                                {
                                    await roomStore.RecordPinnedMessageAsync(agentId, item.ChatId, item.MessageId, true, "add", item.Role, item.Snippet, item.PinnedBy, ct);
                                }
                                list = roomStore.GetPinnedMessagesForAgent(agentId);
                            }
                        }
                    }
                }
                catch { }
            }

            return Results.Ok(new
            {
                agent_id = agentId,
                pinned_message_ids = list.Select(x => x.MessageId).ToArray(),
                pinned_messages = list
            });
        })
        .WithName("GetAgentPinnedMessages")
        .WithSummary("Get all pinned messages across all chat sessions for an agent")
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
            string? detectedChatUserId = request.UserId;
            var consoleChatScope = await ChatVisibilityGuard.ResolveScopeAsync(agentTarget, httpContext, ct);
            if (consoleChatScope is not null)
            {
                string? scopeChatsRaw = null;
                try { scopeChatsRaw = await client.GetAgentChatsRawAsync(agentTarget, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
                if (!ChatVisibilityGuard.CanAccessChat(scopeChatsRaw, effectiveSessionId, consoleChatScope))
                {
                    httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                    await httpContext.Response.WriteAsJsonAsync(new { error = "CHAT_NOT_FOUND", message = $"Chat {effectiveSessionId} not found" }, ct);
                    return;
                }
                detectedChatUserId = consoleChatScope;
            }
            try
            {
                var chatsRaw = await client.GetAgentChatsRawAsync(agentTarget, ct);
                if (!string.IsNullOrWhiteSpace(chatsRaw))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(chatsRaw);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        // Prefer exact UUID match first so multi-user Telegram topics map to the exact chat branch
                        System.Text.Json.JsonElement? matchedEl = null;
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            if (string.Equals(id, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedEl = el;
                                break;
                            }
                        }
                        if (matchedEl is null)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                                if (string.Equals(sid, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedEl = el;
                                    break;
                                }
                            }
                        }
                        if (matchedEl.HasValue)
                        {
                            var m = matchedEl.Value;
                            var sid = m.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                            var ch = m.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
                            var uid = m.TryGetProperty("user_id", out var uProp) ? uProp.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(sid)) qwenPawSessionId = sid;
                            if (!string.IsNullOrWhiteSpace(ch)) detectedChatChannel = ch;
                            if (!string.IsNullOrWhiteSpace(uid) && consoleChatScope is null) detectedChatUserId = uid;
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
                    Channel = detectedChatChannel ?? request.Channel ?? "console",
                    UserId = detectedChatUserId ?? request.UserId
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
            string? detectedChatUserId = request.UserId;
            var consoleChatScope = await ChatVisibilityGuard.ResolveScopeAsync(agentTarget, httpContext, ct);
            if (consoleChatScope is not null)
            {
                string? scopeChatsRaw = null;
                try { scopeChatsRaw = await client.GetAgentChatsRawAsync(agentTarget, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
                if (!ChatVisibilityGuard.CanAccessChat(scopeChatsRaw, effectiveSessionId, consoleChatScope))
                {
                    httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                    await httpContext.Response.WriteAsJsonAsync(new { error = "CHAT_NOT_FOUND", message = $"Chat {effectiveSessionId} not found" }, ct);
                    return;
                }
                detectedChatUserId = consoleChatScope;
            }
            try
            {
                var chatsRaw = await client.GetAgentChatsRawAsync(agentTarget, ct);
                if (!string.IsNullOrWhiteSpace(chatsRaw))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(chatsRaw);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        // Prefer exact UUID match first so multi-user Telegram topics map to the exact chat branch
                        System.Text.Json.JsonElement? matchedEl = null;
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            if (string.Equals(id, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedEl = el;
                                break;
                            }
                        }
                        if (matchedEl is null)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                                if (string.Equals(sid, effectiveSessionId, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedEl = el;
                                    break;
                                }
                            }
                        }
                        if (matchedEl.HasValue)
                        {
                            var m = matchedEl.Value;
                            var sid = m.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                            var ch = m.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
                            var uid = m.TryGetProperty("user_id", out var uProp) ? uProp.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(sid)) qwenPawSessionId = sid;
                            if (!string.IsNullOrWhiteSpace(ch)) detectedChatChannel = ch;
                            if (!string.IsNullOrWhiteSpace(uid) && consoleChatScope is null) detectedChatUserId = uid;
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
                    Channel = detectedChatChannel ?? request.Channel ?? "console",
                    UserId = detectedChatUserId ?? request.UserId
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

        group.MapPost("console/chat/stop", async ([FromBody] ConsoleChatStopRequestDto request, HttpContext httpContext, IQwenPawClient client, SessionQueueManager queueManager, CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(request?.AgentId ?? "mons", httpContext, ct) is { } denied) return denied;
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

        static async Task<IResult> HandleConsoleUploadAsync(
            HttpContext httpContext,
            IWebHostEnvironment env,
            Agent360.API.Features.Agents.Security.SecureUploadStorageService storageService,
            IFleetInstanceRoutingService routing,
            CancellationToken ct)
        {
            if (!httpContext.Request.HasFormContentType)
            {
                return Results.BadRequest(new { message = "Request must be multipart/form-data" });
            }

            var form = await httpContext.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "No valid file uploaded." });
            }
            var (success, error, uploadResponse) = await storageService.StoreFileAsync(
                file,
                env.ContentRootPath,
                ct);

            if (!success || uploadResponse is null)
            {
                return Results.BadRequest(new { error, detail = error });
            }

            var targetAgent = httpContext.Request.Headers["X-Agent-Id"].FirstOrDefault()
                ?? httpContext.Request.Query["agentId"].FirstOrDefault()
                ?? "default";
            targetAgent = FleetInstanceRoutingService.NormalizeAgentId(targetAgent);

            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(targetAgent, httpContext, ct) is { } deniedUpload)
            {
                return deniedUpload;
            }

            var target = routing.ResolveTarget(targetAgent);
            if (target.IsQwenPaw && target.Instance is not null)
            {
                try
                {
                    await using var ms = new MemoryStream();
                    await file.CopyToAsync(ms, ct);
                    var fileBytes = ms.ToArray();
                    var fileName = string.IsNullOrWhiteSpace(file.FileName) ? uploadResponse.Filename : file.FileName;
                    var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? uploadResponse.ContentType : file.ContentType;

                    using var runtimeResp = await routing.SendAsync(
                        target.Instance,
                        HttpMethod.Post,
                        $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}/console/upload",
                        () =>
                        {
                            var multipart = new MultipartFormDataContent();
                            var byteContent = new ByteArrayContent(fileBytes);
                            byteContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                            multipart.Add(byteContent, "file", fileName);
                            return multipart;
                        },
                        target.TargetAgentProfile,
                        TimeSpan.FromSeconds(30),
                        ct: ct);

                    if (runtimeResp.IsSuccessStatusCode)
                    {
                        var runtimeJson = await runtimeResp.Content.ReadAsStringAsync(ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(runtimeJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            var runtimeUrl = doc.RootElement.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                            var runtimeFileName = doc.RootElement.TryGetProperty("file_name", out var fnEl) ? fnEl.GetString() : null;
                            var runtimeSize = doc.RootElement.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out var sz) ? sz : uploadResponse.SizeBytes;

                            if (!string.IsNullOrWhiteSpace(runtimeUrl))
                            {
                                var mediaFileName = Path.GetFileName(runtimeUrl.Replace('\\', '/'));
                                var previewUrl = !string.IsNullOrWhiteSpace(mediaFileName)
                                    ? $"/api/agents/{Uri.EscapeDataString(targetAgent)}/console/files/{Uri.EscapeDataString(mediaFileName)}"
                                    : null;
                                uploadResponse = uploadResponse with
                                {
                                    Url = runtimeUrl,
                                    FileName = !string.IsNullOrWhiteSpace(runtimeFileName) ? runtimeFileName : uploadResponse.FileName,
                                    Size = runtimeSize,
                                    PreviewUrl = previewUrl
                                };
                            }
                        }
                    }
                }
                catch
                {
                    // Graceful fallback to local/S3 uploadResponse if QwenPaw runtime upload is unreachable
                }
            }

            return TypedResults.Ok(uploadResponse);
        }

        routeBuilder.MapPost("console/upload", HandleConsoleUploadAsync)
            .MapToApiVersion(ApiVersion.Default)
            .WithTags(EndpointTags.Agents)
            .WithName("SecureConsoleUploadRoot")
            .WithSummary("Upload file attachment to agent console workspace with extension security check")
            .DisableAntiforgery()
            .AllowAnonymous();

        group.MapPost("console/upload", HandleConsoleUploadAsync)
            .WithName("SecureConsoleUpload")
            .WithSummary("Upload file attachment with virtual path masking and extension security check")
            .DisableAntiforgery()
            .AllowAnonymous();

        group.MapPost("{agentId}/console/upload", (
            string agentId,
            HttpContext httpContext,
            IWebHostEnvironment env,
            Agent360.API.Features.Agents.Security.SecureUploadStorageService storageService,
            IFleetInstanceRoutingService routing,
            CancellationToken ct) =>
        {
            httpContext.Request.Headers["X-Agent-Id"] = agentId;
            return HandleConsoleUploadAsync(httpContext, env, storageService, routing, ct);
        })
        .WithName("SecureAgentConsoleUpload")
        .WithSummary("Upload file attachment scoped to a specific agent console workspace")
        .DisableAntiforgery()
        .AllowAnonymous();

        static async Task<IResult> HandleConsoleFileServeAsync(
            string? agentId,
            string fileName,
            HttpContext httpContext,
            IWebHostEnvironment env,
            IQwenPawClient client,
            CancellationToken ct)
        {
            var normalizedRel = Uri.UnescapeDataString((fileName ?? "").Replace('\\', '/')).Trim('/');
            if (string.IsNullOrWhiteSpace(normalizedRel) || normalizedRel.Contains("..") || !WorkspacePathAcl.IsPathAllowed(normalizedRel))
            {
                return Results.BadRequest(new { error = "Invalid or forbidden file path" });
            }

            var safeFileName = Path.GetFileName(normalizedRel);
            var targetAgent = !string.IsNullOrWhiteSpace(agentId)
                ? agentId
                : (httpContext.Request.Query["agentId"].FirstOrDefault()
                    ?? httpContext.Request.Headers["X-Agent-Id"].FirstOrDefault()
                    ?? "default");
            targetAgent = FleetInstanceRoutingService.NormalizeAgentId(targetAgent);

            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(targetAgent, httpContext, ct) is { } deniedServe)
            {
                return deniedServe;
            }

            var ext = WorkspacePathAcl.ExtractExtension(safeFileName);
            var fallbackContentType = WorkspacePathAcl.GetMimeType(ext);
            var isDownload = string.Equals(httpContext.Request.Query["download"].FirstOrDefault(), "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(httpContext.Request.Query["download"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

            // 1. Try local uploaded copy first (.tmp/agent360-uploads/{fileId}_{sanitizedName}.{ext})
            if (!normalizedRel.Contains('/'))
            {
                try
                {
                    var localUploadsDir = Path.Combine(env.ContentRootPath, ".tmp", "agent360-uploads");
                    if (Directory.Exists(localUploadsDir))
                    {
                        var exactLocal = Path.Combine(localUploadsDir, safeFileName);
                        if (File.Exists(exactLocal))
                        {
                            var bytes = await File.ReadAllBytesAsync(exactLocal, ct);
                            httpContext.Response.Headers.ContentDisposition = $"{(isDownload ? "attachment" : "inline")}; filename=\"{safeFileName.Replace("\"", "")}\"";
                            return Results.Bytes(bytes, fallbackContentType);
                        }
                    }
                }
                catch
                {
                    // Ignore local lookup failure and fall through to QwenPaw workspace media
                }
            }

            // 2. Fetch from QwenPaw workspace (try direct path, media/{normalizedRel}, and common relative paths)
            var candidates = new List<string> { normalizedRel };
            if (!normalizedRel.StartsWith("media/", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"media/{normalizedRel}");
            }
            else
            {
                candidates.Add(normalizedRel["media/".Length..]);
            }
            if (!normalizedRel.StartsWith("generated-images/", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"generated-images/{normalizedRel}");
            }
            else
            {
                candidates.Add(normalizedRel["generated-images/".Length..]);
            }
            if (safeFileName != normalizedRel)
            {
                candidates.Add(safeFileName);
                candidates.Add($"media/{safeFileName}");
                candidates.Add($"generated-images/{safeFileName}");
                candidates.Add($".tmp/{safeFileName}");
            }
            else
            {
                candidates.Add($"generated-images/{safeFileName}");
            }

            foreach (var candidatePath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var resp = await client.DownloadWorkspaceFileAsync(candidatePath, targetAgent, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        var contentType = resp.Content.Headers.ContentType?.ToString();
                        if (string.IsNullOrWhiteSpace(contentType) || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
                        {
                            contentType = fallbackContentType;
                        }
                        httpContext.Response.Headers.ContentDisposition = $"{(isDownload ? "attachment" : "inline")}; filename=\"{safeFileName.Replace("\"", "")}\"";
                        return Results.Bytes(bytes, contentType);
                    }
                }
                catch
                {
                    // Try next candidate
                }
            }

            // 3. Fallback: Fuzzy lookup via agent workspace tree (matches hashed prefix like media/<hash>_{safeFileName} or subfolders)
            try
            {
                var tree = await client.GetWorkspaceTreeAsync(targetAgent, ct, search: safeFileName);
                if (tree?.Entries is { Count: > 0 })
                {
                    var matchedEntry = tree.Entries.FirstOrDefault(e =>
                    {
                        var p = (e.Path ?? "").Replace('\\', '/');
                        var fn = Path.GetFileName(p);
                        return string.Equals(fn, safeFileName, StringComparison.OrdinalIgnoreCase)
                            || fn.EndsWith($"_{safeFileName}", StringComparison.OrdinalIgnoreCase)
                            || fn.Contains(safeFileName, StringComparison.OrdinalIgnoreCase);
                    });

                    if (matchedEntry is not null && !string.IsNullOrWhiteSpace(matchedEntry.Path))
                    {
                        using var fuzzyResp = await client.DownloadWorkspaceFileAsync(matchedEntry.Path, targetAgent, ct);
                        if (fuzzyResp.IsSuccessStatusCode)
                        {
                            var bytes = await fuzzyResp.Content.ReadAsByteArrayAsync(ct);
                            var contentType = fuzzyResp.Content.Headers.ContentType?.ToString();
                            if (string.IsNullOrWhiteSpace(contentType) || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
                            {
                                contentType = fallbackContentType;
                            }
                            httpContext.Response.Headers.ContentDisposition = $"{(isDownload ? "attachment" : "inline")}; filename=\"{safeFileName.Replace("\"", "")}\"";
                            return Results.Bytes(bytes, contentType);
                        }
                    }
                }
            }
            catch
            {
                // Fallback lookup failed
            }

            return Results.NotFound(new { message = $"Uploaded file '{safeFileName}' not found in workspace" });
        }

        routeBuilder.MapGet("console/files/{**fileName}", (
            string fileName,
            [FromQuery] string? agentId,
            HttpContext httpContext,
            IWebHostEnvironment env,
            IQwenPawClient client,
            CancellationToken ct) => HandleConsoleFileServeAsync(agentId, fileName, httpContext, env, client, ct))
            .MapToApiVersion(ApiVersion.Default)
            .WithTags(EndpointTags.Agents)
            .WithName("GetConsoleUploadedFileRoot")
            .WithSummary("Stream or preview an uploaded console attachment inline")
            .AllowAnonymous();

        group.MapGet("console/files/{**fileName}", (
            string fileName,
            [FromQuery] string? agentId,
            HttpContext httpContext,
            IWebHostEnvironment env,
            IQwenPawClient client,
            CancellationToken ct) => HandleConsoleFileServeAsync(agentId, fileName, httpContext, env, client, ct))
            .WithName("GetConsoleUploadedFile")
            .WithSummary("Stream or preview an uploaded console attachment inline")
            .AllowAnonymous();

        group.MapGet("{agentId}/console/files/{**fileName}", (
            string agentId,
            string fileName,
            HttpContext httpContext,
            IWebHostEnvironment env,
            IQwenPawClient client,
            CancellationToken ct) => HandleConsoleFileServeAsync(agentId, fileName, httpContext, env, client, ct))
            .WithName("GetAgentConsoleUploadedFile")
            .WithSummary("Stream or preview an agent-scoped uploaded console attachment inline")
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
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            httpContext.RequestServices.GetService<SessionMultiplexerHub>()?.TouchSubscribedAgent(agentId);
            try
            {
                var chatsJson = await client.GetAgentChatsRawAsync(agentId, ct);
                chatsJson = ChatVisibilityGuard.FilterChatsJson(chatsJson, await ChatVisibilityGuard.ResolveScopeAsync(agentId, httpContext, ct));
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
                        var rawChatName = node["name"]?.ToString();
                        if (!string.IsNullOrEmpty(rawChatName))
                        {
                            var ctxMarkerIdx = rawChatName.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                            if (ctxMarkerIdx >= 0) rawChatName = rawChatName[..ctxMarkerIdx].TrimEnd();
                            rawChatName = System.Text.RegularExpressions.Regex.Replace(rawChatName, @"^\[Replying to [^:\]]+:\s*""[^""]*""\]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                            node["name"] = rawChatName;
                        }
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
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden or >= System.Net.HttpStatusCode.InternalServerError)
            {
                // Non-AgentCore runtimes or unhosted agents legitimately have no chats
                return TypedResults.Ok(Array.Empty<AgentSessionResponse>());
            }
            catch (HttpRequestException ex)
            {
                var status = (int)(ex.StatusCode ?? System.Net.HttpStatusCode.BadGateway);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: status is >= 400 and < 600 ? status : StatusCodes.Status502BadGateway,
                    title: "Agent chat list temporarily unavailable");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Agent chat list temporarily unavailable");
            }
        })
        .WithName("GetAgentChats")
        .WithSummary("List all chat sessions for a specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/chats/{chatId}", async (string agentId, string chatId, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            if (await ChatVisibilityGuard.EnforceChatAsync(agentId, chatId, httpContext, client, ct) is { } hidden) return hidden;
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
                if (chatId.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Content("{\"messages\":[],\"status\":\"idle\"}", "application/json");
                }
                return Results.NotFound(new { message = $"Chat {chatId} for agent {agentId} not found on infra" });
            }
        })
        .WithName("GetAgentChatHistory")
        .WithSummary("Get chat session messages for specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/skills", async (string agentId, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            return await RuntimeResultAsync(async () => TypedResults.Ok(await client.GetSkillsAsync(agentId, ct)));
        })
        .WithName("ListAgentSkills")
        .WithSummary("List skills belonging to a specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/workspace/tree", async (
            string agentId,
            [FromQuery] string? category,
            [FromQuery] string? ext,
            [FromQuery] string? search,
            [FromQuery] bool? includeSystem,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            return await RuntimeResultAsync(async () => TypedResults.Ok(await client.GetWorkspaceTreeAsync(agentId, ct, category, ext, search, includeSystem ?? false)));
        })
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
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            return await RuntimeResultAsync(async () =>
            {
                var targetPath = path;
                var resp = await client.DownloadWorkspaceFileAsync(targetPath, agentId, ct);
                
                // Fallback: If not found directly (even if prefixed with media/ or subfolder), fuzzy lookup by bare file name in workspace tree
                if (!resp.IsSuccessStatusCode)
                {
                    var bareName = Path.GetFileName(path.Replace('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(bareName))
                    {
                        resp.Dispose();
                        // Try bareName at root, media/{bareName}, and generated-images/{bareName}
                        foreach (var altCandidate in new[] { bareName, $"media/{bareName}", $"generated-images/{bareName}" })
                        {
                            if (string.Equals(altCandidate, path, StringComparison.OrdinalIgnoreCase)) continue;
                            resp.Dispose();
                            resp = await client.DownloadWorkspaceFileAsync(altCandidate, agentId, ct);
                            if (resp.IsSuccessStatusCode)
                            {
                                targetPath = altCandidate;
                                break;
                            }
                        }

                        if (!resp.IsSuccessStatusCode)
                        {
                            var tree = await client.GetWorkspaceTreeAsync(agentId, ct, search: bareName);
                            var matched = tree?.Entries?.FirstOrDefault(e =>
                            {
                                var fn = Path.GetFileName((e.Path ?? "").Replace('\\', '/'));
                                return string.Equals(fn, bareName, StringComparison.OrdinalIgnoreCase)
                                    || fn.EndsWith($"_{bareName}", StringComparison.OrdinalIgnoreCase);
                            });
                            if (matched is not null && !string.IsNullOrWhiteSpace(matched.Path))
                            {
                                resp.Dispose();
                                targetPath = matched.Path;
                                resp = await client.DownloadWorkspaceFileAsync(targetPath, agentId, ct);
                            }
                        }
                    }
                }

                if (!resp.IsSuccessStatusCode)
                {
                    resp.Dispose();
                    return Results.NotFound(new { message = $"File '{path}' not found" });
                }

                using (resp)
                {
                    var ext = WorkspacePathAcl.ExtractExtension(targetPath);
                    var contentType = resp.Content.Headers.ContentType?.ToString() ?? WorkspacePathAcl.GetMimeType(ext);
                    var fileName = Path.GetFileName(path.Replace('\\', '/'));
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

                    if (inline == true)
                    {
                        httpContext.Response.Headers.ContentDisposition = $"inline; filename=\"{fileName.Replace("\"", "")}\"";
                        return Results.Bytes(bytes, contentType);
                    }

                    return Results.File(bytes, contentType, fileName);
                }
            });
        })
        .WithName("DownloadAgentWorkspaceFile")
        .WithSummary("Download or preview a workspace artifact file for specific agent")
        .AllowAnonymous();

        // Universal Structured File Preview (XLSX, CSV, TSV, DOCX, PPTX, RTF, ZIP, HTML, Markdown, Code, Text)
        async Task<IResult> HandleWorkspaceFilePreviewAsync(
            string agentId,
            string path,
            HttpContext httpContext,
            IWebHostEnvironment env,
            IQwenPawClient client,
            CancellationToken ct)
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
            var normalizedRel = Uri.UnescapeDataString((path ?? "").Replace('\\', '/')).Trim('/');
            if (string.IsNullOrWhiteSpace(normalizedRel) || normalizedRel.Contains("..") || !WorkspacePathAcl.IsPathAllowed(normalizedRel))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            var safeFileName = Path.GetFileName(normalizedRel);
            var targetAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId);
            var inlineUrl = $"/api/agents/{Uri.EscapeDataString(targetAgent)}/console/files/{string.Join("/", normalizedRel.Split('/').Select(Uri.EscapeDataString))}";

            // 1. Check local uploads (.tmp/agent360-uploads)
            if (!normalizedRel.Contains('/'))
            {
                try
                {
                    var localUploadsDir = Path.Combine(env.ContentRootPath, ".tmp", "agent360-uploads");
                    if (Directory.Exists(localUploadsDir))
                    {
                        var exactLocal = Path.Combine(localUploadsDir, safeFileName);
                        if (File.Exists(exactLocal))
                        {
                            var localBytes = await File.ReadAllBytesAsync(exactLocal, ct);
                            return TypedResults.Ok(Agent360.API.Features.Agents.Workspaces.UniversalFilePreviewService.BuildPreview(localBytes, normalizedRel, inlineUrl));
                        }
                    }
                }
                catch
                {
                    // Fall through to workspace lookup
                }
            }

            // 2. Candidate paths in QwenPaw workspace
            var candidates = new List<string> { normalizedRel };
            if (!normalizedRel.StartsWith("media/", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"media/{normalizedRel}");
            }
            else
            {
                candidates.Add(normalizedRel["media/".Length..]);
            }
            if (!normalizedRel.StartsWith("generated-images/", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"generated-images/{normalizedRel}");
            }
            else
            {
                candidates.Add(normalizedRel["generated-images/".Length..]);
            }
            if (safeFileName != normalizedRel)
            {
                candidates.Add(safeFileName);
                candidates.Add($"media/{safeFileName}");
                candidates.Add($"generated-images/{safeFileName}");
                candidates.Add($".tmp/{safeFileName}");
            }
            else
            {
                candidates.Add($"generated-images/{safeFileName}");
            }

            foreach (var candidatePath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var resp = await client.DownloadWorkspaceFileAsync(candidatePath, targetAgent, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        return TypedResults.Ok(Agent360.API.Features.Agents.Workspaces.UniversalFilePreviewService.BuildPreview(bytes, candidatePath, inlineUrl));
                    }
                }
                catch
                {
                    // Try next candidate
                }
            }

            // 3. Fuzzy lookup via workspace tree
            try
            {
                var tree = await client.GetWorkspaceTreeAsync(targetAgent, ct, search: safeFileName);
                var matched = tree?.Entries?.FirstOrDefault(e =>
                {
                    var fn = Path.GetFileName((e.Path ?? "").Replace('\\', '/'));
                    return string.Equals(fn, safeFileName, StringComparison.OrdinalIgnoreCase)
                        || fn.EndsWith($"_{safeFileName}", StringComparison.OrdinalIgnoreCase)
                        || fn.Contains(safeFileName, StringComparison.OrdinalIgnoreCase);
                });
                if (matched is not null && !string.IsNullOrWhiteSpace(matched.Path))
                {
                    using var fuzzyResp = await client.DownloadWorkspaceFileAsync(matched.Path, targetAgent, ct);
                    if (fuzzyResp.IsSuccessStatusCode)
                    {
                        var bytes = await fuzzyResp.Content.ReadAsByteArrayAsync(ct);
                        return TypedResults.Ok(Agent360.API.Features.Agents.Workspaces.UniversalFilePreviewService.BuildPreview(bytes, matched.Path, inlineUrl));
                    }
                }
            }
            catch
            {
                // Ignore
            }

            return Results.NotFound(new { message = $"File '{path}' not found" });
        }

        group.MapGet("workspace/file-preview", (
            [FromQuery] string path,
            [FromQuery] string? agentId,
            HttpContext httpContext,
            IWebHostEnvironment env,
            IQwenPawClient client,
            CancellationToken ct) => HandleWorkspaceFilePreviewAsync(agentId ?? "mons", path, httpContext, env, client, ct))
            .WithName("GetWorkspaceFilePreview")
            .WithSummary("Generate rich structured preview for any workspace artifact (XLSX, CSV, DOCX, PPTX, ZIP, HTML, Markdown, Code)")
            .AllowAnonymous();

        group.MapGet("{agentId}/workspace/file-preview", (
            string agentId,
            [FromQuery] string path,
            HttpContext httpContext,
            IWebHostEnvironment env,
            IQwenPawClient client,
            CancellationToken ct) => HandleWorkspaceFilePreviewAsync(agentId, path, httpContext, env, client, ct))
            .WithName("GetAgentWorkspaceFilePreview")
            .WithSummary("Generate rich structured preview for any agent workspace artifact (XLSX, CSV, DOCX, PPTX, ZIP, HTML, Markdown, Code)")
            .AllowAnonymous();

        group.MapGet("{agentId}/workspace/signed-file-url", async (
            string agentId,
            [FromQuery] string path,
            [FromQuery] bool? inline,
            HttpContext httpContext,
            IConfiguration config,
            IOptionsMonitor<Agent360SecurityOptions> security,
            CancellationToken ct) =>
        {
            var currentUser = httpContext.RequestServices.GetService<CurrentUser>()
                ?? new CurrentUser(new HttpContextAccessor { HttpContext = httpContext });
            var (accessAllowed, accessReason) = await AgentOwnershipGuard.CheckAccessAsync(
                agentId, httpContext, currentUser, config, ct);
            if (!accessAllowed)
            {
                return Results.Problem(accessReason ?? "Agent access denied", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
            }
            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem("Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden Path");
            }

            var opt = security.CurrentValue;
            var secret = string.IsNullOrWhiteSpace(opt.OperatorToken) ? "skyer-file-download-token" : opt.OperatorToken;
            var expUnix = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds();
            var cleanPath = path.Trim().Replace('\\', '/');
            var payload = $"{cleanPath}:{expUnix}";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            var sig = hex[..Math.Min(16, hex.Length)];

            var query = $"?path={Uri.EscapeDataString(cleanPath)}&exp={expUnix}&sig={sig}";
            if (inline == true) query += "&inline=true";

            var signedUrl = $"/api/agents/{Uri.EscapeDataString(agentId)}/workspace/file-download{query}";
            return TypedResults.Ok(new
            {
                url = signedUrl,
                expiresAt = expUnix,
                expiresInSeconds = 8 * 3600
            });
        })
        .WithName("GetSignedAgentWorkspaceFileUrl")
        .WithSummary("Generate a signed, expiring URL for anonymous artifact viewing or download")
        .AllowAnonymous();


        group.MapGet("{agentId}/workspace/file-content", async (string agentId, [FromQuery] string path, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
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

        group.MapPut("{agentId}/workspace/file-content", async (
            string agentId,
            [FromQuery] string path,
            [FromBody] SetWorkspaceFileContentRequestDto request,
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

            if (!WorkspacePathAcl.IsPathAllowed(path))
            {
                return Results.Problem(
                    detail: "Access denied by Agent360 Workspace Path ACL: requested path contains private or sensitive tokens.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Path");
            }

            var ok = await client.SaveFileContentAsync(path, request.Content ?? "", agentId, ct);
            return ok ? TypedResults.Ok(new { path, saved = true }) : Results.Problem("Failed to save file content");
        })
        .WithName("SetAgentFileContent")
        .WithSummary("Update workspace file content for specific agent")
        .AllowAnonymous();

        group.MapGet("{agentId}/active-model", async (
            string agentId,
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

            var res = await client.GetAgentActiveModelAsync(agentId, ct);
            if (res is null)
            {
                var cleanSlug = FleetInstanceRoutingService.NormalizeAgentId(agentId);
                return Results.NotFound(new { detail = $"Agent '{cleanSlug}' not found in fleet" });
            }

            return TypedResults.Ok(res);
        })
        .WithName("GetAgentActiveModel")
        .WithSummary("Get current active LLM model provider and model for agent")
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

        group.MapMethods("{agentId}/pin", ["PATCH", "POST", "PUT"], async (
            string agentId,
            [FromBody] PinAgentRequest request,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true &&
                await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied)
            {
                return denied;
            }

            var res = await client.PinAgentAsync(agentId, request.Pinned, ct);
            if (res is null)
            {
                return Results.Problem("Failed to update agent pin state on QwenPaw runtime", statusCode: 502);
            }

            var multiplexerHub = httpContext.RequestServices.GetService<SessionMultiplexerHub>();
            if (multiplexerHub is not null)
            {
                var normAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
                var currentUser = httpContext.RequestServices.GetService<Agent360.API.Identity.CurrentUser>();
                var externalSub = currentUser?.ExternalUserId;
                if (string.IsNullOrWhiteSpace(externalSub))
                {
                    externalSub = httpContext.User?.FindFirst("sub")?.Value;
                }
                _ = multiplexerHub.BroadcastAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "agent_pin_updated",
                    agent_id = normAgent,
                    pinned = request.Pinned,
                    user_id = currentUser?.UserId,
                    user_sub = externalSub,
                    timestamp = DateTimeOffset.UtcNow.ToString("O")
                }));
            }

            return TypedResults.Ok(res);
        })
        .WithName("PinAgent")
        .WithSummary("Pin or unpin an agent profile on QwenPaw runtime")
        .AllowAnonymous();

        // 2e. Native Context Management & Compaction (/workspace/checkpoints/gc and /compact trigger)
        group.MapPost("compact-context", async (
            [FromBody] CompactContextRequestDto request,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(request.AgentId ?? "mons", httpContext, ct) is { } denied) return denied;
            var res = await client.CompactContextAsync(request, ct);
            return TypedResults.Ok(res);
        })
        .WithName("CompactAgentContext")
        .WithSummary("Perform context compaction and garbage collection on agent runtime")
        .AllowAnonymous();

        group.MapPost("{agentId}/compact-context", async (
            string agentId,
            [FromBody] CompactContextRequestDto request,
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;
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
            HttpContext httpContext,
            IQwenPawClient client,
            CancellationToken ct) =>
        {
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId ?? "mons", httpContext, ct) is { } denied) return denied;
            var receipts = await client.GetToolCallReceiptsAsync(sessionId, agentId, ct);
            return TypedResults.Ok(receipts);
        })
        .WithName("GetToolCallReceipts")
        .WithSummary("Retrieve verified tool call receipts for a session")
        .AllowAnonymous();

        // 3. Root Agent Profile Detail Route (Non-shadowing with literal guard)
        group.MapGet("{agentId}", async (string agentId, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            if (ReservedLiteralSegments.Contains(agentId))
            {
                return Results.NotFound(new { message = $"Route '{agentId}' is not an agent profile." });
            }
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;

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

        group.MapMethods("{agentId}", ["PUT", "PATCH"], async (string agentId, HttpContext httpContext, IQwenPawClient client, CancellationToken ct) =>
        {
            if (ReservedLiteralSegments.Contains(agentId))
            {
                return Results.NotFound(new { message = $"Route '{agentId}' is not an agent profile." });
            }
            if (await AgentOwnershipGuard.EnforceOrForbidAsync(agentId, httpContext, ct) is { } denied) return denied;

            System.Text.Json.Nodes.JsonObject? patch = null;
            try
            {
                patch = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.Nodes.JsonObject>(httpContext.Request.Body, cancellationToken: ct);
            }
            catch (System.Text.Json.JsonException) { }
            patch ??= new System.Text.Json.Nodes.JsonObject();
            patch["approval_level"] = "OFF";

            try
            {
                var updated = await client.UpdateAgentProfileRawAsync(agentId, patch, ct);
                if (updated is null)
                {
                    return Results.NotFound(new { message = $"Agent profile {agentId} not found on infra" });
                }
                return Results.Content(Agent360.API.Features.Agents.Security.SecretRedaction.RedactJson(updated), "application/json");
            }
            catch (HttpRequestException)
            {
                return Results.NotFound(new { message = $"Agent profile {agentId} not found on infra" });
            }
        })
        .WithName("UpdateAgentProfile")
        .WithSummary("Update agent profile configuration on infra (always enforces approval_level=OFF)")
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

        var explicitMentions = Agent360.API.Features.Agents.SubAgents.SessionRoomOrchestrator.DetectMentionedCollaborators(rawUserText, agentTarget);
        var roomOrchestrator = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomOrchestrator>();
        var triageDecision = roomOrchestrator is not null
            ? roomOrchestrator.DecideRoomCollaboratorsForTurn(agentTarget, effectiveSessionId, qwenPawSessionId, rawUserText, explicitMentions)
            : new Agent360.API.Features.Agents.SubAgents.HostRoomTriageDecision(explicitMentions.Count > 0, explicitMentions, explicitMentions.Count > 0 ? "explicit_mention" : "no_room_collaborators", "");
        var mentionedCollaborators = triageDecision.ShouldUseRoom ? triageDecision.SelectedCollaborators : [];

        string? activeRoomRunId = null;
        int activeRoomFinalRound = 1;
        var existingMediaContentItems = effectiveRequest.Input?.FirstOrDefault()?.Content?
            .Where(c => !string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        var roomStore = httpContext.RequestServices.GetService<Agent360.API.Features.Agents.SubAgents.SessionRoomStore>();
        var existingSessionRoom = roomStore?.GetByChat(agentTarget, effectiveSessionId)
            ?? (!string.IsNullOrWhiteSpace(qwenPawSessionId) ? roomStore?.GetByChat(agentTarget, qwenPawSessionId) : null);
        bool hasActiveMultiAgentRoom = Agent360.API.Features.Agents.SubAgents.SessionRoomStore.IsMultiAgentRoom(existingSessionRoom);

        Task? soloRoomInitTask = null;
        if (mentionedCollaborators.Count == 0 && roomOrchestrator is not null && hasActiveMultiAgentRoom)
        {
            var soloSmartContext = roomOrchestrator.BuildSoloSmartContextInjection(
                agentTarget, effectiveSessionId, qwenPawSessionId, rawUserText, triageDecision);
            if (!string.IsNullOrWhiteSpace(soloSmartContext) && !string.IsNullOrWhiteSpace(rawUserText))
            {
                var enrichedSoloInput = $"{rawUserText}\n\n{soloSmartContext}";
                var mergedSoloContent = new List<ConsoleChatContentItem> { new("text", enrichedSoloInput) };
                mergedSoloContent.AddRange(existingMediaContentItems);
                effectiveRequest = effectiveRequest with
                {
                    Message = enrichedSoloInput,
                    Input =
                    [
                        new ConsoleChatInputItem(
                            "user",
                            "message",
                            mergedSoloContent)
                    ]
                };
            }

            // Only record Manager intake turn when this session ALREADY has an active multi-agent Room
            soloRoomInitTask = Task.Run(async () =>
            {
                try
                {
                    await roomOrchestrator.RecordManagerTriageTurnAsync(agentTarget, effectiveSessionId, qwenPawSessionId, rawUserText, triageDecision, CancellationToken.None);
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

            // 1. Zero-Latency Triage Feedback: immediately emit host_triage SSE event BEFORE Matrix HTTP provisioning
            if (!ct.IsCancellationRequested)
            {
                var preTriageEvt = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @object = "room_event",
                    status = "host_triage",
                    triage_owner_id = triageDecision.TriageOwnerId,
                    collaborator_id = mentionedCollaborators.FirstOrDefault() ?? agentTarget,
                    triage_mode = triageDecision.Mode,
                    triage_reason = triageDecision.Reason,
                    triage_confidence = triageDecision.Confidence,
                    selected_collaborators = mentionedCollaborators,
                    assigned_agents = triageDecision.AssignedAgents,
                    auto_recruited_collaborators = triageDecision.AutoRecruitedCollaborators,
                    collaborator_focuses = triageDecision.CollaboratorFocuses,
                    synthesis_owner_id = triageDecision.SynthesisOwnerId ?? agentTarget,
                    host_should_self_execute = triageDecision.HostShouldSelfExecute,
                    host_execution_role = triageDecision.HostExecutionRole,
                    host_execution_focus = triageDecision.HostExecutionFocus
                });
                try
                {
                    await httpContext.Response.WriteAsync($"data: {preTriageEvt}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch {}
            }

            // 2. Ensure single Session Room once for all selected collaborators, and sync title in background so turn execution never waits
            var baseRoom = await roomOrchestrator.EnsureRoomAsync(
                agentTarget,
                effectiveSessionId,
                mentionedCollaborators.FirstOrDefault(),
                qwenPawSessionId,
                upstreamCts.Token,
                mentionedCollaborators);
            if (string.IsNullOrWhiteSpace(baseRoom.RoomTitle) && !string.IsNullOrWhiteSpace(rawUserText))
            {
                _ = Task.Run(async () =>
                {
                    try { await roomOrchestrator.SyncRoomTitleAsync(agentTarget, effectiveSessionId, rawUserText, CancellationToken.None); }
                    catch {}
                });
            }

            var recentSessionHistory = new System.Text.StringBuilder();
            try
            {
                var histRaw = await client.GetChatHistoryRawAsync(effectiveSessionId, agentTarget, upstreamCts.Token);
                if (!string.IsNullOrWhiteSpace(histRaw))
                {
                    using var hDoc = System.Text.Json.JsonDocument.Parse(histRaw);
                    if (hDoc.RootElement.TryGetProperty("messages", out var mArr) && mArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var textTurns = new List<(string Role, string Text)>();
                        foreach (var item in mArr.EnumerateArray())
                        {
                            var mType = item.TryGetProperty("type", out var tP) ? tP.GetString() : "message";
                            if (mType is not (null or "" or "message")) continue;
                            var mRole = item.TryGetProperty("role", out var rP) ? rP.GetString() ?? "assistant" : "assistant";
                            string? mText = null;
                            if (item.TryGetProperty("content", out var cP))
                            {
                                if (cP.ValueKind == System.Text.Json.JsonValueKind.String)
                                    mText = cP.GetString();
                                else if (cP.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    mText = string.Join("\n", cP.EnumerateArray().Where(x => !x.TryGetProperty("type", out var tp) || tp.GetString() == "text").Select(x => x.TryGetProperty("text", out var tx) ? tx.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)));
                            }
                            if (!string.IsNullOrWhiteSpace(mText))
                            {
                                var idx = mText.IndexOf("[SHARED SESSION ROOM CONTEXT]", StringComparison.OrdinalIgnoreCase);
                                if (idx >= 0) mText = mText[..idx].TrimEnd();
                                if (mText.Length > 360) mText = mText[..360] + "...";
                                textTurns.Add((mRole == "user" ? "Người dùng" : $"Host (@{agentTarget})", mText.Trim()));
                            }
                        }
                        foreach (var (speaker, snippet) in textTurns.TakeLast(5))
                        {
                            recentSessionHistory.AppendLine($"- {speaker}: {snippet}");
                        }
                    }
                }
            }
            catch {}

            var accumulatedContext = new System.Text.StringBuilder();
            Agent360.API.Features.Agents.SubAgents.SessionCollaborationRoom? latestRoom = baseRoom;
            var firstCollab = mentionedCollaborators.FirstOrDefault() ?? agentTarget;

            if (!ct.IsCancellationRequested)
            {
                var roomInitEvt = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @object = "room_event",
                    status = "collaborator_running",
                    collaborator_id = firstCollab,
                    step_index = 1,
                    total_collaborators = mentionedCollaborators.Count,
                    execution_strategy = triageDecision.ExecutionStrategy,
                    triage_mode = triageDecision.Mode,
                    triage_reason = triageDecision.Reason,
                    triage_confidence = triageDecision.Confidence,
                    selected_collaborators = mentionedCollaborators,
                    room = baseRoom
                });
                try
                {
                    await httpContext.Response.WriteAsync($"data: {roomInitEvt}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch {}
            }

            activeRoomRunId = $"run_{Guid.NewGuid():N}"[..16];

            // 2b. Narrator Bubble 1 (host_kickoff + inline receipt "Messaged @A and @B"):
            // Emit immediately before waiting on specialist A2A calls so user sees Host leading the conversation right away.
            var (kickoffRoom, kickoffTurn) = await roomOrchestrator.RecordHostKickoffTurnAsync(
                agentTarget,
                effectiveSessionId,
                mentionedCollaborators,
                rawUserText,
                triageDecision,
                activeRoomRunId,
                qwenPawSessionId,
                upstreamCts.Token);
            latestRoom = kickoffRoom;

            if (!ct.IsCancellationRequested)
            {
                var kickoffEvt = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @object = "room_turn",
                    status = kickoffRoom.Status,
                    collaborator_id = agentTarget,
                    run_id = activeRoomRunId,
                    round_no = 1,
                    visibility = "main",
                    receipt = kickoffTurn.Receipt,
                    room = kickoffRoom,
                    turn = kickoffTurn
                });
                try
                {
                    await httpContext.Response.WriteAsync($"data: {kickoffEvt}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch {}
            }

            var baseHistorySnippet = recentSessionHistory.Length > 0
                ? $"### Các lượt trao đổi gần đây trong phiên chat:\n{recentSessionHistory}"
                : null;

            int completedStepCount = 0;
            var (waveRoom, waveTurns) = await roomOrchestrator.ExecuteCollaboratorWaveAsync(
                agentTarget,
                effectiveSessionId,
                mentionedCollaborators,
                rawUserText ?? "",
                baseHistorySnippet,
                qwenPawSessionId,
                triageDecision.CollaboratorFocuses,
                triageDecision.ExecutionStrategy,
                async (completedRoom, collabTurn, finishedCollab) =>
                {
                    completedStepCount++;
                    if (!ct.IsCancellationRequested)
                    {
                        var roomTurnEvt = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            @object = "room_turn",
                            status = completedRoom.Status,
                            collaborator_id = finishedCollab,
                            run_id = activeRoomRunId,
                            round_no = 1,
                            visibility = "room",
                            step_index = completedStepCount,
                            total_collaborators = mentionedCollaborators.Count,
                            execution_strategy = triageDecision.ExecutionStrategy,
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
                },
                upstreamCts.Token,
                activeRoomRunId,
                1);

            latestRoom = waveRoom;
            var allWaveTurns = new List<Agent360.API.Features.Agents.SubAgents.SessionRoomTurn>(waveTurns);

            // 2c. Check if Manager decides a Round 2 cross-examination wave is warranted
            var followUpDecision = roomOrchestrator.DecideFollowUpRound(
                latestRoom ?? baseRoom,
                rawUserText,
                triageDecision,
                waveTurns);

            var effectiveWaveCollaborators = mentionedCollaborators.ToList();
            if (followUpDecision.ShouldRunRound2)
            {
                activeRoomFinalRound = 2;
                var round2Collaborators = followUpDecision.AdditionalCollaborators is { Count: > 0 }
                    ? followUpDecision.AdditionalCollaborators
                    : mentionedCollaborators;
                foreach (var addCollab in round2Collaborators)
                {
                    if (!effectiveWaveCollaborators.Contains(addCollab, StringComparer.OrdinalIgnoreCase))
                        effectiveWaveCollaborators.Add(addCollab);
                }

                var round1Summary = string.Join("\n", waveTurns.Select(t => $"- @{t.AuthorId} ({t.AuthorName}): {t.Text}"));
                var round2Context = baseHistorySnippet is not null
                    ? $"{baseHistorySnippet}\n\n### Kết quả Vòng 1 của các chuyên viên trong Room (để kiểm tra chéo / hỗ trợ bổ sung ở Vòng 2):\n{round1Summary}"
                    : $"### Kết quả Vòng 1 của các chuyên viên trong Room (để kiểm tra chéo / hỗ trợ bổ sung ở Vòng 2):\n{round1Summary}";

                int round2StepCount = 0;
                var (round2Room, round2Turns) = await roomOrchestrator.ExecuteCollaboratorWaveAsync(
                    agentTarget,
                    effectiveSessionId,
                    round2Collaborators,
                    rawUserText ?? "",
                    round2Context,
                    qwenPawSessionId,
                    followUpDecision.Round2Focuses,
                    triageDecision.ExecutionStrategy,
                    async (completedRoom, collabTurn, finishedCollab) =>
                    {
                        round2StepCount++;
                        if (!ct.IsCancellationRequested)
                        {
                            var roomTurnEvt = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                @object = "room_turn",
                                status = completedRoom.Status,
                                collaborator_id = finishedCollab,
                                run_id = activeRoomRunId,
                                round_no = 2,
                                visibility = "room",
                                step_index = round2StepCount,
                                total_collaborators = round2Collaborators.Count,
                                execution_strategy = triageDecision.ExecutionStrategy,
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
                    },
                    upstreamCts.Token,
                    activeRoomRunId,
                    2);

                latestRoom = round2Room;
                allWaveTurns.AddRange(round2Turns);
            }

            // 2d. Narrator Bubble 2 (host_progress + inline receipt "N messages with @A and @B" or "Message from @A"):
            var progressRoundNo = effectiveWaveCollaborators.Count > 1 ? Math.Max(2, activeRoomFinalRound) : 1;
            var (progressRoom, progressTurn) = await roomOrchestrator.RecordHostProgressTurnAsync(
                agentTarget,
                effectiveSessionId,
                effectiveWaveCollaborators,
                rawUserText,
                triageDecision,
                allWaveTurns,
                activeRoomRunId,
                progressRoundNo,
                qwenPawSessionId,
                upstreamCts.Token);
            latestRoom = progressRoom;

            if (!ct.IsCancellationRequested)
            {
                var progressEvt = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @object = "room_turn",
                    status = progressRoom.Status,
                    collaborator_id = agentTarget,
                    run_id = activeRoomRunId,
                    round_no = progressRoundNo,
                    visibility = "main",
                    receipt = progressTurn.Receipt,
                    room = progressRoom,
                    turn = progressTurn
                });
                try
                {
                    await httpContext.Response.WriteAsync($"data: {progressEvt}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch {}
            }

            foreach (var ctTurn in allWaveTurns)
            {
                accumulatedContext.AppendLine($"- [Vòng {ctTurn.RoundNo}] @{ctTurn.AuthorId} ({ctTurn.AuthorName}): {ctTurn.Text}");
            }

            var cleanUserObjective = Agent360.API.Features.Agents.SubAgents.SessionRoomOrchestrator.StripCollaboratorMentions(rawUserText, mentionedCollaborators);
            var managerSynthesisBrief = roomOrchestrator.BuildManagerSynthesisBrief(latestRoom ?? baseRoom, cleanUserObjective, triageDecision, allWaveTurns);

            if (latestRoom is not null && !ct.IsCancellationRequested)
            {
                var hostSynthEvt = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @object = "room_event",
                    status = "synthesizing",
                    collaborator_id = agentTarget,
                    run_id = activeRoomRunId,
                    round_no = activeRoomFinalRound,
                    synthesis_owner_id = triageDecision.SynthesisOwnerId ?? agentTarget,
                    host_should_self_execute = triageDecision.HostShouldSelfExecute,
                    host_execution_role = triageDecision.HostExecutionRole,
                    host_execution_focus = triageDecision.HostExecutionFocus,
                    manager_brief = managerSynthesisBrief,
                    room = latestRoom
                });
                try
                {
                    await httpContext.Response.WriteAsync($"data: {hostSynthEvt}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch {}
            }

            var hostSelfExecNote = triageDecision.HostShouldSelfExecute
                ? $"Manager quyết định bạn (@{agentTarget}) VỪA TỰ THỰC THI phần việc chuyên môn của riêng mình ({triageDecision.HostExecutionFocus}) VỪA LÀ NGƯỜI ĐẠI DIỆN ROOM TỔNG HỢP CUỐI sau khi các thành viên khác đã chạy xong."
                : $"Manager quyết định các thành viên trong Room (@{string.Join(", @", mentionedCollaborators)}) đã chạy xong (done) toàn bộ phần việc chuyên môn; bạn (@{agentTarget}) KHÔNG cần tự chạy lại nhánh công cụ riêng mà tập trung 100% vào vai trò NGƯỜI ĐẠI DIỆN ROOM TỔNG HỢP CUỐI.";
            var triageHeader = triageDecision.Mode == "explicit_mention"
                ? $"AgentTeams Manager (@agentteams-manager) đã tiếp nhận yêu cầu trong Room, điều phối @{string.Join(", @", mentionedCollaborators)} chạy xong phần việc được chỉ định và ưu tiên Chủ session (@{agentTarget}) làm Người đại diện Room tổng hợp kết quả cuối. {hostSelfExecNote}"
                : $"AgentTeams Manager (@agentteams-manager) đã điều phối @{string.Join(", @", mentionedCollaborators)} trong Room chạy xong chuyên môn và ưu tiên Chủ session (@{agentTarget}) làm Người đại diện Room tổng hợp kết quả cuối ({triageDecision.Reason}). {hostSelfExecNote}";
            var synthesizedInput = $"{rawUserText}\n\n[SHARED SESSION ROOM CONTEXT]\nLưu ý hệ thống: {triageHeader}\n{managerSynthesisBrief}\n\nCác chuyên viên AI trong Room đã hoàn tất (done) phần chạy của mình và qua kiểm định của AgentTeams Manager:\n{accumulatedContext}\nVới vai trò Chủ session kiêm Người đại diện Room (@{agentTarget}), hãy thực hiện đúng quyết định của Manager ở trên và đưa ra câu phản hồi tổng hợp cuối cùng hoàn chỉnh, mạch lạc và trực tiếp nhất cho người dùng.";
            var mergedRoomContent = new List<ConsoleChatContentItem> { new("text", synthesizedInput) };
            mergedRoomContent.AddRange(existingMediaContentItems);
            effectiveRequest = effectiveRequest with
            {
                Message = synthesizedInput,
                Input =
                [
                    new ConsoleChatInputItem(
                        "user",
                        "message",
                        mergedRoomContent)
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

            if (roomOrchestrator is not null && (mentionedCollaborators.Count > 0 || hasActiveMultiAgentRoom))
            {
                var capturedSoloInit = soloRoomInitTask;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (capturedSoloInit is not null)
                        {
                            try { await capturedSoloInit; } catch {}
                        }
                        client.InvalidateChatList(agentTarget);
                        var histRaw = await client.GetChatHistoryRawAsync(effectiveSessionId, agentTarget, CancellationToken.None);
                        if (!string.IsNullOrWhiteSpace(histRaw))
                        {
                            using var hDoc = System.Text.Json.JsonDocument.Parse(histRaw);
                            if (hDoc.RootElement.TryGetProperty("messages", out var mArr) && mArr.ValueKind == System.Text.Json.JsonValueKind.Array && mArr.GetArrayLength() > 0)
                            {
                                string? synthText = null;
                                string? synthReasoning = null;
                                for (int i = mArr.GetArrayLength() - 1; i >= 0; i--)
                                {
                                    var item = mArr[i];
                                    var r = item.TryGetProperty("role", out var rP) ? rP.GetString() : null;
                                    if (r == "user") break;
                                    if (r == "assistant")
                                    {
                                        var t = item.TryGetProperty("type", out var tP) ? tP.GetString() : "message";
                                        var txt = item.TryGetProperty("content", out var cP) && cP.ValueKind == System.Text.Json.JsonValueKind.Array
                                            ? string.Join("\n", cP.EnumerateArray().Select(x => x.TryGetProperty("text", out var tx) ? tx.GetString() : null).Where(x => !string.IsNullOrEmpty(x)))
                                            : null;
                                        if (t == "message" && string.IsNullOrWhiteSpace(synthText) && !string.IsNullOrWhiteSpace(txt))
                                            synthText = txt;
                                        else if (t == "reasoning" && string.IsNullOrWhiteSpace(synthReasoning) && !string.IsNullOrWhiteSpace(txt))
                                            synthReasoning = txt;
                                    }
                                }
                                if (!string.IsNullOrWhiteSpace(synthText))
                                {
                                    await roomOrchestrator.RecordHostSynthesisTurnAsync(
                                        agentTarget, effectiveSessionId, synthText, synthReasoning, null, qwenPawSessionId, CancellationToken.None, activeRoomRunId, activeRoomFinalRound);
                                }
                            }
                        }
                    }
                    catch {}
                });
            }
            else
            {
                client.InvalidateChatList(agentTarget);
            }

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
        Agent360.API.Features.Agents.SubAgents.SessionRoomStore roomStore) =>
        roomStore.EnrichChatHistoryJson(historyRaw, agentId, chatId);

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
