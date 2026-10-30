using System.Text.Json;
using Agent360.API.Identity;
using Npgsql;

namespace Agent360.API.Features.Agents.Security;

/// <summary>
/// Per-user chat isolation for agents a member can reach only through the shared "Anyone" (UserId = -1) assignment.
/// Admins and directly assigned owners/operators see every chat; shared-access members see and touch only chats whose
/// QwenPaw <c>user_id</c> equals their own owner key, and new chats they start are stamped with that key.
/// </summary>
public static class ChatVisibilityGuard
{
    public const string OwnerKeyPrefix = "skyer:";

    /// <summary>Owner key used as QwenPaw user_id for chats created by an authenticated Skyer user.</summary>
    public static string? OwnerKeyFor(CurrentUser currentUser) =>
        currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.ExternalUserId)
            ? OwnerKeyPrefix + currentUser.ExternalUserId
            : null;

    /// <summary>
    /// Returns null when the caller may see every chat of the agent; otherwise the owner key the caller is limited to
    /// (an empty string means "no identity": the caller may see nothing).
    /// </summary>
    public static async Task<string?> ResolveScopeAsync(string? agentSlug, HttpContext context, CancellationToken ct = default)
    {
        var cacheKey = "agent360.chat-scope:" + (agentSlug ?? "").ToLowerInvariant();
        if (context.Items.TryGetValue(cacheKey, out var cached)) return cached as string;

        var scope = await ComputeScopeAsync(agentSlug, context, ct);
        context.Items[cacheKey] = scope;
        return scope;
    }

    private static async Task<string?> ComputeScopeAsync(string? agentSlug, HttpContext context, CancellationToken ct)
    {
        var currentUser = context.RequestServices.GetRequiredService<CurrentUser>();
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        if (await AgentOwnershipGuard.IsEffectiveAdminAsync(context, currentUser, config, ct)) return null;

        var ownerKey = OwnerKeyFor(currentUser);
        if (currentUser.UserId is not > 0) return ownerKey ?? "";

        var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connStr)) return ownerKey ?? "";

        var candidateSlugs = AgentOwnershipGuard.ExpandEquivalentAgentSlugs(agentSlug)
            .SelectMany(s => new[] { s.ToLowerInvariant(), $"bot:agent360:{s.ToLowerInvariant()}" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                @"SELECT 1 FROM user_management.""AgentAssignments""
                  WHERE ""UserId"" = $1 AND LOWER(""AgentSlug"") = ANY($2) LIMIT 1;", conn);
            cmd.Parameters.AddWithValue(currentUser.UserId.Value);
            cmd.Parameters.AddWithValue(candidateSlugs);
            if (await cmd.ExecuteScalarAsync(ct) is not null) return null; // direct owner/operator: full visibility
        }
        catch
        {
            // Fail closed to the per-user scope.
        }
        return ownerKey ?? "";
    }

    public static bool IsVisible(string? chatUserId, string? scope) =>
        scope is null || (scope.Length > 0 && string.Equals(chatUserId, scope, StringComparison.Ordinal));

    /// <summary>Filters a raw QwenPaw chat list JSON array to the caller's scope.</summary>
    public static string FilterChatsJson(string chatsJson, string? scope)
    {
        if (scope is null || string.IsNullOrWhiteSpace(chatsJson)) return chatsJson;
        try
        {
            using var doc = JsonDocument.Parse(chatsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "[]";
            var kept = doc.RootElement.EnumerateArray()
                .Where(el => IsVisible(el.TryGetProperty("user_id", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null, scope))
                .Select(el => el.GetRawText());
            return "[" + string.Join(",", kept) + "]";
        }
        catch (JsonException)
        {
            return "[]";
        }
    }

    /// <summary>
    /// Ownership check for one chat. Unknown chats (not yet created on the runtime) are allowed so a member can start
    /// a new session; an existing chat owned by someone else is denied.
    /// </summary>
    public static bool CanAccessChat(string? chatsJson, string chatId, string? scope)
    {
        if (scope is null) return true;
        if (string.IsNullOrWhiteSpace(chatsJson)) return scope.Length > 0;
        try
        {
            using var doc = JsonDocument.Parse(chatsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return scope.Length > 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
                var sid = el.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                if (!string.Equals(id, chatId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sid, chatId, StringComparison.OrdinalIgnoreCase)) continue;
                var uid = el.TryGetProperty("user_id", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                return IsVisible(uid, scope);
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return scope.Length > 0;
    }

    /// <summary>Returns a 404 result when the caller may not access the chat (avoids leaking chat existence).</summary>
    public static async Task<IResult?> EnforceChatAsync(
        string? agentSlug, string chatId, HttpContext context, Services.IQwenPawClient client, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(agentSlug, context, ct);
        if (scope is null) return null;
        string? chatsJson = null;
        try { chatsJson = await client.GetAgentChatsRawAsync(agentSlug ?? "mons", ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return CanAccessChat(chatsJson, chatId, scope)
            ? null
            : Results.NotFound(new { message = $"Chat {chatId} not found" });
    }
}
