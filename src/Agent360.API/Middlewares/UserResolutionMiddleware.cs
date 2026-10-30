using Hmz.Core.SharedKernel.Authorization;
using Agent360.API.Identity;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.Infrastructure.Authorization;
using Agent360.UserManagement.Infrastructure.Persistence;
using Agent360.UserManagement.UseCase.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Agent360.API.Middlewares;

public sealed class UserResolutionMiddleware(
    RequestDelegate next,
    ILogger<UserResolutionMiddleware> logger,
    IConfiguration configuration)
{
    public async Task InvokeAsync(
        HttpContext context,
        IUserIdentityService userIdentityService,
        CurrentUser currentUser,
        PermissionSnapshotResolver permissionResolver,
        IPermissionSnapshotAccessor snapshotAccessor,
        IServiceScopeFactory scopeFactory,
        LogtoManagementClient logto,
        Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache,
        Microsoft.Extensions.Caching.Hybrid.HybridCache hybridCache)
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
                            var logtoProfile = await logto.GetUserAsync(currentUser.ExternalUserId, context.RequestAborted);
                            if (logtoProfile is not null)
                            {
                                if (string.IsNullOrWhiteSpace(email)) email = logtoProfile.Email;
                                if (string.IsNullOrWhiteSpace(name)) name = logtoProfile.Name;
                                if (string.IsNullOrWhiteSpace(avatarUrl)) avatarUrl = logtoProfile.AvatarUrl;
                            }
                        }

                        var finalEmail = !string.IsNullOrWhiteSpace(email) ? email : $"{currentUser.ExternalUserId}@skyer.hmz.one";
                        var finalName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : finalEmail.Split('@')[0];
                        var parts = finalName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        var firstName = parts.Length > 0 ? parts[0] : finalName;
                        var lastName = parts.Length > 1 ? parts[1] : "";

                        // Access tokens carry no role claims; the user's Logto "admin" role decides the initial local role.
                        var isAdmin = await logto.IsLogtoAdminAsync(currentUser.ExternalUserId, context.RequestAborted) == true;
                        var defaultRoleId = isAdmin ? AdminRoleId : CustomerRoleId;

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
                await ReconcileAdminRoleAsync(userId, currentUser.ExternalUserId, logto, memoryCache, hybridCache, context.RequestAborted);

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
                    await HealPlaceholderUserRecordIfNeededAsync(userId, currentUser.ExternalUserId, logto, context.RequestAborted);
                }
            }
        }

        await next(context);
    }

    private async Task HealPlaceholderUserRecordIfNeededAsync(long userId, string externalUserId, LogtoManagementClient logto, CancellationToken ct)
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

            var logtoProfile = await logto.GetUserAsync(externalUserId, ct);
            if (logtoProfile is null) return;

            var realEmail = !string.IsNullOrWhiteSpace(logtoProfile.Email) ? logtoProfile.Email : currentEmail;
            var realName = !string.IsNullOrWhiteSpace(logtoProfile.Name) ? logtoProfile.Name : currentDisplayName;
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
            updCmd.Parameters.AddWithValue((object?)logtoProfile.AvatarUrl ?? DBNull.Value);
            updCmd.Parameters.AddWithValue(userId);
            await updCmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not self-heal placeholder user profile for {ExternalUserId}", externalUserId);
        }
    }

    private const long CustomerRoleId = 1L;
    private const long AdminRoleId = 2L;
    private static readonly TimeSpan RoleSyncInterval = TimeSpan.FromMinutes(2);

    public static string RoleSyncCacheKey(string externalUserId) => $"logto:role-sync:{externalUserId}";

    /// <summary>
    /// Keeps the local platform role in line with the user's Logto "admin" role (the identity provider owns
    /// admin membership; the admin UI writes role changes to Logto first). Throttled per user; when Logto cannot
    /// be read the local role is left unchanged. SYSTEM users are never touched.
    /// </summary>
    private async Task ReconcileAdminRoleAsync(
        long userId,
        string externalUserId,
        LogtoManagementClient logto,
        Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache,
        Microsoft.Extensions.Caching.Hybrid.HybridCache hybridCache,
        CancellationToken ct)
    {
        var syncKey = RoleSyncCacheKey(externalUserId);
        if (memoryCache.TryGetValue(syncKey, out _)) return;

        var logtoAdmin = await logto.IsLogtoAdminAsync(externalUserId, ct);
        if (logtoAdmin is null)
        {
            // Logto unreachable or unknown subject: retry soon, never change roles on an error.
            memoryCache.Set(syncKey, true, TimeSpan.FromSeconds(20));
            return;
        }

        var connStr = configuration.GetConnectionString("DefaultConnection") ?? configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connStr)) return;

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var codes = new List<string>();
            await using (var cmd = new NpgsqlCommand(
                @"SELECT UPPER(r.""Code"") FROM user_management.""UserRoles"" ur
                  JOIN user_management.""Roles"" r ON ur.""RoleId"" = r.""Id""
                  WHERE ur.""UserId"" = $1;", conn))
            {
                cmd.Parameters.AddWithValue(userId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) codes.Add(reader.GetString(0));
            }

            if (codes.Contains("SYSTEM"))
            {
                memoryCache.Set(syncKey, true, RoleSyncInterval);
                return;
            }

            var localAdmin = codes.Contains("ADMIN");
            if (localAdmin != logtoAdmin.Value)
            {
                var targetRoleId = logtoAdmin.Value ? AdminRoleId : CustomerRoleId;
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var del = new NpgsqlCommand(
                    @"DELETE FROM user_management.""UserRoles"" WHERE ""UserId"" = $1 AND ""RoleId"" IN ($2, $3);", conn, tx))
                {
                    del.Parameters.AddWithValue(userId);
                    del.Parameters.AddWithValue(CustomerRoleId);
                    del.Parameters.AddWithValue(AdminRoleId);
                    await del.ExecuteNonQueryAsync(ct);
                }
                await using (var ins = new NpgsqlCommand(
                    @"INSERT INTO user_management.""UserRoles"" (""UserId"", ""RoleId"", ""Id"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy"")
                      VALUES ($1, $2, $1, NOW(), 1, NOW(), 1) ON CONFLICT DO NOTHING;", conn, tx))
                {
                    ins.Parameters.AddWithValue(userId);
                    ins.Parameters.AddWithValue(targetRoleId);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await using (var stamp = new NpgsqlCommand(
                    @"UPDATE user_management.""Users"" SET ""PermissionStamp"" = NOW() WHERE ""Id"" = $1;", conn, tx))
                {
                    stamp.Parameters.AddWithValue(userId);
                    await stamp.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);

                await hybridCache.RemoveAsync($"um:permissions:user:{userId}", ct);
                await hybridCache.RemoveByTagAsync($"um:permissions:user:{userId}", ct);
                logger.LogInformation("Reconciled local role of user {UserId} ({ExternalUserId}) to {Role} from Logto", userId, externalUserId, logtoAdmin.Value ? "ADMIN" : "CUSTOMER");
            }

            memoryCache.Set(syncKey, true, RoleSyncInterval);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Role reconciliation from Logto failed for user {UserId}", userId);
        }
    }
}
