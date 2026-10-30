using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Hmz.Core.SharedKernel.Authorization;
using Agent360.API.Identity;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.Infrastructure.Authorization;
using Agent360.UserManagement.Infrastructure.Persistence;
using Agent360.UserManagement.UseCase.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agent360.API.Middlewares;

public sealed class UserResolutionMiddleware(
    RequestDelegate next,
    ILogger<UserResolutionMiddleware> logger,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    public async Task InvokeAsync(
        HttpContext context,
        IUserIdentityService userIdentityService,
        CurrentUser currentUser,
        PermissionSnapshotResolver permissionResolver,
        IPermissionSnapshotAccessor snapshotAccessor,
        IServiceScopeFactory scopeFactory)
    {
        if (currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
        {
            var localId = await userIdentityService.ResolveLocalUserIdAsync(currentUser.ExternalUserId, context.RequestAborted);

            // JIT auto-provision user if they authenticate via Logto but do not exist in local DB yet
            if (localId is null)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var existing = await db.Users.FirstOrDefaultAsync(u => u.LogtoUserId == currentUser.ExternalUserId, context.RequestAborted);
                    if (existing is null)
                    {
                        var email = context.User.FindFirst("email")?.Value;
                        var name = context.User.FindFirst("name")?.Value;
                        var avatarUrl = context.User.FindFirst("picture")?.Value ?? context.User.FindFirst("avatar")?.Value;

                        // Logto access_token for an API resource only carries sub/aud/scope and omits email/name/picture.
                        // Fetch the real identity from Logto Management API on first sign-in / sign-up!
                        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name))
                        {
                            var logtoProfile = await FetchLogtoUserProfileAsync(currentUser.ExternalUserId, context.RequestAborted);
                            if (logtoProfile is not null)
                            {
                                if (string.IsNullOrWhiteSpace(email)) email = logtoProfile.Value.Email;
                                if (string.IsNullOrWhiteSpace(name)) name = logtoProfile.Value.Name;
                                if (string.IsNullOrWhiteSpace(avatarUrl)) avatarUrl = logtoProfile.Value.AvatarUrl;
                            }
                        }

                        var finalEmail = !string.IsNullOrWhiteSpace(email) ? email : $"{currentUser.ExternalUserId}@skyer.hmz.one";
                        var finalName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : finalEmail.Split('@')[0];
                        var parts = finalName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        var firstName = parts.Length > 0 ? parts[0] : finalName;
                        var lastName = parts.Length > 1 ? parts[1] : "";

                        // Default to Customer role (Id 1) or Admin role (Id 2) if admin claim present
                        var isAdmin = context.User.FindAll("roles").Concat(context.User.FindAll(System.Security.Claims.ClaimTypes.Role))
                            .Any(r => string.Equals(r.Value, "admin", StringComparison.OrdinalIgnoreCase));
                        var defaultRoleId = isAdmin ? 2L : 1L;

                        var newUser = User.Register(
                            currentUser.ExternalUserId,
                            finalEmail,
                            defaultRoleId,
                            firstName,
                            lastName,
                            avatarUrl
                        );

                        db.Users.Add(newUser);
                        await db.SaveChangesAsync(context.RequestAborted);
                        localId = newUser.Id;
                        logger.LogInformation("Auto-provisioned local user {UserId} ({Email} / {DisplayName}) for Logto sub {ExternalUserId}", localId, finalEmail, finalName, currentUser.ExternalUserId);
                    }
                    else
                    {
                        localId = existing.Id;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to auto-provision user for external ID '{ExternalUserId}'", currentUser.ExternalUserId);
                }
            }

            currentUser.SetResolvedUserId(localId);

            if (localId is { } userId)
            {
                try
                {
                    snapshotAccessor.Set(await permissionResolver.ResolveAsync(userId, context.RequestAborted));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not resolve permission snapshot for user {UserId}", userId);
                }

                // If caller is requesting their profile (/users/me) and their stored DB record still has placeholder
                // values ("User Skyer" or "{sub}@skyer.hmz.one"), self-heal it once from Logto Management API!
                if (context.Request.Path.Value?.EndsWith("/users/me", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await HealPlaceholderUserRecordIfNeededAsync(userId, currentUser.ExternalUserId, context.RequestAborted);
                }
            }
        }

        await next(context);
    }

    private async Task HealPlaceholderUserRecordIfNeededAsync(long userId, string externalUserId, CancellationToken ct)
    {
        var connStr = configuration.GetConnectionString("DefaultConnection") ?? configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connStr)) return;

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            string? currentEmail = null;
            string? currentDisplayName = null;
            await using (var checkCmd = new NpgsqlCommand(
                @"SELECT ""Email"", ""DisplayName"" FROM user_management.""Users"" WHERE ""Id"" = $1 LIMIT 1;",
                conn))
            {
                checkCmd.Parameters.AddWithValue(userId);
                await using var reader = await checkCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    currentEmail = reader.IsDBNull(0) ? null : reader.GetString(0);
                    currentDisplayName = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            var isPlaceholderEmail = string.IsNullOrWhiteSpace(currentEmail) ||
                currentEmail.Equals($"{externalUserId}@skyer.hmz.one", StringComparison.OrdinalIgnoreCase);
            var isPlaceholderName = string.IsNullOrWhiteSpace(currentDisplayName) ||
                currentDisplayName.Equals("User Skyer", StringComparison.OrdinalIgnoreCase) ||
                currentDisplayName.Equals("User", StringComparison.OrdinalIgnoreCase);

            if (!isPlaceholderEmail && !isPlaceholderName) return;

            var logtoProfile = await FetchLogtoUserProfileAsync(externalUserId, ct);
            if (logtoProfile is null) return;

            var realEmail = !string.IsNullOrWhiteSpace(logtoProfile.Value.Email) ? logtoProfile.Value.Email : currentEmail;
            var realName = !string.IsNullOrWhiteSpace(logtoProfile.Value.Name) ? logtoProfile.Value.Name : currentDisplayName;
            if (string.IsNullOrWhiteSpace(realEmail) && string.IsNullOrWhiteSpace(realName)) return;

            var parts = (realName ?? "User").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var firstName = parts.Length > 0 ? parts[0] : "User";
            var lastName = parts.Length > 1 ? parts[1] : "";

            await using var updCmd = new NpgsqlCommand(@"
                UPDATE user_management.""Users""
                SET ""Email"" = COALESCE(NULLIF($1, ''), ""Email""),
                    ""DisplayName"" = COALESCE(NULLIF($2, ''), ""DisplayName""),
                    ""FirstName"" = COALESCE(NULLIF($3, ''), ""FirstName""),
                    ""LastName"" = $4,
                    ""AvatarUrl"" = COALESCE(NULLIF($5, ''), ""AvatarUrl""),
                    ""ModifiedAt"" = NOW()
                WHERE ""Id"" = $6;
            ", conn);
            updCmd.Parameters.AddWithValue((object?)realEmail ?? DBNull.Value);
            updCmd.Parameters.AddWithValue((object?)realName ?? DBNull.Value);
            updCmd.Parameters.AddWithValue(firstName);
            updCmd.Parameters.AddWithValue(lastName);
            updCmd.Parameters.AddWithValue((object?)logtoProfile.Value.AvatarUrl ?? DBNull.Value);
            updCmd.Parameters.AddWithValue(userId);
            await updCmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not self-heal placeholder user profile for {ExternalUserId}", externalUserId);
        }
    }

    private async Task<(string? Email, string? Name, string? AvatarUrl)?> FetchLogtoUserProfileAsync(string externalUserId, CancellationToken ct)
    {
        try
        {
            var endpoint = "https://auth-skyer.hmz.one";
            var m2mId = "skyer-m2m-backend";
            var m2mSecret = "7pPJ85w2sM3l7CtgxTVZdGz7WEtvBb9P";

            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(8);

            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/oidc/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = m2mId,
                    ["client_secret"] = m2mSecret,
                    ["resource"] = "https://default.logto.app/api",
                    ["scope"] = "all"
                })
            };
            tokenReq.Headers.Add("User-Agent", "Agent360/1.0");

            using var tokenResp = await httpClient.SendAsync(tokenReq, ct);
            if (!tokenResp.IsSuccessStatusCode) return null;

            var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonObject>(ct);
            var mgmtToken = tokenJson?["access_token"]?.ToString();
            if (string.IsNullOrWhiteSpace(mgmtToken)) return null;

            using var userReq = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/api/users/{Uri.EscapeDataString(externalUserId)}");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mgmtToken);
            userReq.Headers.Add("User-Agent", "Agent360/1.0");

            using var userResp = await httpClient.SendAsync(userReq, ct);
            if (!userResp.IsSuccessStatusCode) return null;

            var u = await userResp.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (u is null) return null;

            var identities = u["identities"] as JsonObject;
            var googleDetails = identities?["google"]?["details"] as JsonObject;

            var email = u["primaryEmail"]?.ToString()
                ?? googleDetails?["email"]?.ToString();
            var name = u["name"]?.ToString()
                ?? googleDetails?["name"]?.ToString()
                ?? u["username"]?.ToString()
                ?? email?.Split('@')[0];
            var avatarUrl = u["avatar"]?.ToString()
                ?? googleDetails?["avatar"]?.ToString();

            return (email, name, avatarUrl);
        }
        catch
        {
            return null;
        }
    }
}
