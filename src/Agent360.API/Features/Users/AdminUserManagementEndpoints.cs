using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Identity;
using Npgsql;

namespace Agent360.API.Features.Users;

public sealed record AdminUserDto(
    long Id,
    string LogtoUserId,
    string? Email,
    string FirstName,
    string LastName,
    string? DisplayName,
    int Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles);

public sealed record UpdateUserRoleRequest(long RoleId);

public sealed record RegisterOrSyncUserRequest(
    string LogtoUserId,
    string? Email,
    string? DisplayName,
    string? Role = "MEMBER"
);


public sealed class AdminUserManagementEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("admin/users")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Admin.Users")
            .RequireAuthorization("Admin");

        // List all users with their roles
        group.MapGet("", async (IConfiguration config, CancellationToken ct) =>
        {
            var usersMap = new Dictionary<long, (AdminUserDto Dto, List<string> Roles)>();
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var sql = @"
                SELECT 
                    u.""Id"", u.""LogtoUserId"", u.""Email"", u.""FirstName"", u.""LastName"", 
                    u.""DisplayName"", u.""Status"", u.""CreatedAt"", r.""Code""
                FROM user_management.""Users"" u
                LEFT JOIN user_management.""UserRoles"" ur ON u.""Id"" = ur.""UserId""
                LEFT JOIN user_management.""Roles"" r ON ur.""RoleId"" = r.""Id""
                WHERE u.""IsDeleted"" = false
                ORDER BY u.""Id"" ASC;
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var logtoUserId = reader.GetString(1);
                var email = reader.IsDBNull(2) ? null : reader.GetString(2);
                var firstName = reader.GetString(3);
                var lastName = reader.GetString(4);
                var displayName = reader.IsDBNull(5) ? null : reader.GetString(5);
                var status = reader.GetInt32(6);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(7);
                var roleCode = reader.IsDBNull(8) ? "MEMBER" : reader.GetString(8);

                if (!usersMap.TryGetValue(id, out var entry))
                {
                    entry = (new AdminUserDto(id, logtoUserId, email, firstName, lastName, displayName, status, createdAt, new List<string>()), new List<string>());
                    usersMap[id] = entry;
                }

                if (!string.IsNullOrWhiteSpace(roleCode) && !entry.Roles.Contains(roleCode))
                {
                    entry.Roles.Add(roleCode);
                }
            }

            var result = usersMap.Values
                .Select(v => v.Dto with { Roles = v.Roles })
                .ToList();

            return Results.Ok(result);
        })
        .WithName("AdminListUsers")
        .WithSummary("List all users with roles (Admin only)");

        // Change user role (Admin = 2, Member = 1)
        group.MapPut("{id:long}/role", async (long id, UpdateUserRoleRequest req, IConfiguration config, Microsoft.Extensions.Caching.Hybrid.HybridCache cache, LogtoManagementClient logto, Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache, CancellationToken ct) =>
        {
            if (req.RoleId is not (1 or 2))
            {
                return Results.BadRequest(new { error = "RoleId must be 1 (Customer) or 2 (Admin)" });
            }

            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            string? logtoUserId = null;
            await using (var subCmd = new NpgsqlCommand(@"SELECT ""LogtoUserId"" FROM user_management.""Users"" WHERE ""Id"" = $1;", conn))
            {
                subCmd.Parameters.AddWithValue(id);
                logtoUserId = await subCmd.ExecuteScalarAsync(ct) as string;
            }
            if (string.IsNullOrWhiteSpace(logtoUserId)) return Results.NotFound();

            // Logto owns admin membership (users are reconciled from it on sign-in), so write it there first.
            if (!await logto.SetAdminAsync(logtoUserId, req.RoleId == 2, ct))
            {
                return Results.Problem("Could not update the user's admin role in Logto; no changes were made.", statusCode: StatusCodes.Status502BadGateway);
            }

            // Delete existing roles for this user and insert new role
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var delCmd = new NpgsqlCommand(@"DELETE FROM user_management.""UserRoles"" WHERE ""UserId"" = $1;", conn, tx))
                {
                    delCmd.Parameters.AddWithValue(id);
                    await delCmd.ExecuteNonQueryAsync(ct);
                }

                await using (var insCmd = new NpgsqlCommand(@"
                    INSERT INTO user_management.""UserRoles"" (""UserId"", ""RoleId"", ""Id"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy"")
                    VALUES ($1, $2, $1, NOW(), 1, NOW(), 1);
                ", conn, tx))
                {
                    insCmd.Parameters.AddWithValue(id);
                    insCmd.Parameters.AddWithValue(req.RoleId);
                    await insCmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                await cache.RemoveAsync($"um:permissions:user:{id}", ct);
                await cache.RemoveByTagAsync($"um:permissions:user:{id}", ct);
                memoryCache.Remove(Agent360.API.Middlewares.UserResolutionMiddleware.RoleSyncCacheKey(logtoUserId));
                return Results.Ok(new { success = true, userId = id, roleId = req.RoleId });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(ex.Message);
            }
        })
        .WithName("AdminUpdateUserRole")
        .WithSummary("Update user role (Admin only)");
        // Upsert/Sync a registered user manually or from frontend hook
        group.MapPost("sync", async (RegisterOrSyncUserRequest req, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LogtoUserId))
            {
                return Results.BadRequest(new { error = "LogtoUserId is required" });
            }

            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            // 1. Check if user already exists
            long userId = 0;
            await using (var checkCmd = new NpgsqlCommand(
                @"SELECT ""Id"" FROM user_management.""Users"" WHERE ""LogtoUserId"" = $1 OR (""Email"" IS NOT NULL AND ""Email"" = $2) LIMIT 1;",
                conn))
            {
                checkCmd.Parameters.AddWithValue(req.LogtoUserId);
                checkCmd.Parameters.AddWithValue((object?)req.Email ?? DBNull.Value);
                var obj = await checkCmd.ExecuteScalarAsync(ct);
                if (obj is long lid) userId = lid;
                else if (obj is int iid) userId = iid;
            }

            var email = !string.IsNullOrWhiteSpace(req.Email) ? req.Email : $"{req.LogtoUserId}@skyer.hmz.one";
            var displayName = !string.IsNullOrWhiteSpace(req.DisplayName) ? req.DisplayName : email.Split('@')[0];
            var parts = displayName.Split(' ', 2);
            var firstName = parts[0];
            var lastName = parts.Length > 1 ? parts[1] : "Skyer";
            var roleId = string.Equals(req.Role, "ADMIN", StringComparison.OrdinalIgnoreCase) ? 2L : 1L;

            if (userId == 0)
            {
                // Insert new user
                await using var insCmd = new NpgsqlCommand(@"
                    INSERT INTO user_management.""Users"" (
                        ""LogtoUserId"", ""Email"", ""FirstName"", ""LastName"", ""DisplayName"", ""Status"", ""PermissionStamp"", ""IsDeleted"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy"", ""DeletedBy""
                    ) VALUES (
                        $1, $2, $3, $4, $5, 1, NOW(), false, NOW(), 1, NOW(), 1, 0
                    ) RETURNING ""Id"";
                ", conn);
                insCmd.Parameters.AddWithValue(req.LogtoUserId);
                insCmd.Parameters.AddWithValue((object?)email ?? DBNull.Value);
                insCmd.Parameters.AddWithValue(firstName);
                insCmd.Parameters.AddWithValue(lastName);
                insCmd.Parameters.AddWithValue(displayName);
                var newIdObj = await insCmd.ExecuteScalarAsync(ct);
                userId = Convert.ToInt64(newIdObj);

                // Insert UserRoles
                await using var roleCmd = new NpgsqlCommand(@"
                    INSERT INTO user_management.""UserRoles"" (""UserId"", ""RoleId"", ""Id"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy"")
                    VALUES ($1, $2, $1, NOW(), 1, NOW(), 1)
                    ON CONFLICT DO NOTHING;
                ", conn);
                roleCmd.Parameters.AddWithValue(userId);
                roleCmd.Parameters.AddWithValue(roleId);
                await roleCmd.ExecuteNonQueryAsync(ct);

                return Results.Ok(new { success = true, created = true, userId, logtoUserId = req.LogtoUserId });
            }
            else
            {
                // Update existing user profile if email / displayName improved
                await using var updCmd = new NpgsqlCommand(@"
                    UPDATE user_management.""Users""
                    SET ""LogtoUserId"" = $1,
                        ""Email"" = COALESCE(NULLIF($2, ''), ""Email""),
                        ""DisplayName"" = COALESCE(NULLIF($3, ''), ""DisplayName""),
                        ""ModifiedAt"" = NOW()
                    WHERE ""Id"" = $4;
                ", conn);
                updCmd.Parameters.AddWithValue(req.LogtoUserId);
                updCmd.Parameters.AddWithValue((object?)email ?? DBNull.Value);
                updCmd.Parameters.AddWithValue(displayName);
                updCmd.Parameters.AddWithValue(userId);
                await updCmd.ExecuteNonQueryAsync(ct);

                return Results.Ok(new { success = true, created = false, userId, logtoUserId = req.LogtoUserId });
            }
        })
        .WithName("AdminSyncUser")
        .WithSummary("Sync or upsert user into database (Admin only)");

        // Automatically pull and sync all users from Logto Management API into local PostgreSQL
        group.MapPost("sync-from-logto", async (IConfiguration config, LogtoManagementClient logto, Microsoft.Extensions.Caching.Hybrid.HybridCache cache, Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            var usersArray = await logto.ListUsersAsync(ct);
            if (usersArray is null)
            {
                return Results.Problem("Failed to fetch users from Logto Management API", statusCode: StatusCodes.Status502BadGateway);
            }
            if (usersArray.Count == 0)
            {
                return Results.Ok(new { synced = 0, message = "No users found in Logto" });
            }

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            int syncedCount = 0, rolesChanged = 0;
            foreach (var u in usersArray)
            {
                var profile = LogtoManagementClient.ParseUser(u);
                var sub = profile.Id;
                if (string.IsNullOrWhiteSpace(sub)) continue;

                var email = profile.Email ?? $"{sub}@skyer.hmz.one";
                var name = profile.Name ?? email.Split('@')[0];
                var parts = name.Split(' ', 2);
                var firstName = parts[0];
                var lastName = parts.Length > 1 ? parts[1] : "";

                // null = Logto roles unreadable: keep the local role as it is.
                var logtoAdmin = await logto.IsLogtoAdminAsync(sub, ct);

                long userId = 0;
                await using (var checkCmd = new NpgsqlCommand(
                    @"SELECT ""Id"" FROM user_management.""Users"" WHERE ""LogtoUserId"" = $1 LIMIT 1;", conn))
                {
                    checkCmd.Parameters.AddWithValue(sub);
                    var obj = await checkCmd.ExecuteScalarAsync(ct);
                    if (obj is long lid) userId = lid;
                    else if (obj is int iid) userId = iid;
                }

                if (userId == 0)
                {
                    await using var insCmd = new NpgsqlCommand(@"
                        INSERT INTO user_management.""Users"" (
                            ""LogtoUserId"", ""Email"", ""FirstName"", ""LastName"", ""DisplayName"", ""AvatarUrl"", ""Status"", ""PermissionStamp"", ""IsDeleted"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy"", ""DeletedBy""
                        ) VALUES (
                            $1, $2, $3, $4, $5, $6, 1, NOW(), false, NOW(), 1, NOW(), 1, 0
                        ) RETURNING ""Id"";
                    ", conn);
                    insCmd.Parameters.AddWithValue(sub);
                    insCmd.Parameters.AddWithValue(email);
                    insCmd.Parameters.AddWithValue(firstName);
                    insCmd.Parameters.AddWithValue(lastName);
                    insCmd.Parameters.AddWithValue(name);
                    insCmd.Parameters.AddWithValue((object?)profile.AvatarUrl ?? DBNull.Value);
                    userId = Convert.ToInt64(await insCmd.ExecuteScalarAsync(ct));

                    await using var roleCmd = new NpgsqlCommand(@"
                        INSERT INTO user_management.""UserRoles"" (""UserId"", ""RoleId"", ""Id"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy"")
                        VALUES ($1, $2, $1, NOW(), 1, NOW(), 1)
                        ON CONFLICT DO NOTHING;
                    ", conn);
                    roleCmd.Parameters.AddWithValue(userId);
                    roleCmd.Parameters.AddWithValue(logtoAdmin == true ? 2L : 1L);
                    await roleCmd.ExecuteNonQueryAsync(ct);
                }
                else
                {
                    await using var updCmd = new NpgsqlCommand(@"
                        UPDATE user_management.""Users""
                        SET ""Email"" = COALESCE(NULLIF($2, ''), ""Email""),
                            ""DisplayName"" = COALESCE(NULLIF($3, ''), ""DisplayName""),
                            ""AvatarUrl"" = COALESCE(NULLIF($4, ''), ""AvatarUrl""),
                            ""ModifiedAt"" = NOW()
                        WHERE ""Id"" = $1;
                    ", conn);
                    updCmd.Parameters.AddWithValue(userId);
                    updCmd.Parameters.AddWithValue(email);
                    updCmd.Parameters.AddWithValue(name);
                    updCmd.Parameters.AddWithValue((object?)profile.AvatarUrl ?? "");
                    await updCmd.ExecuteNonQueryAsync(ct);

                    if (logtoAdmin is not null)
                    {
                        var codes = new List<string>();
                        await using (var rc = new NpgsqlCommand(
                            @"SELECT UPPER(r.""Code"") FROM user_management.""UserRoles"" ur JOIN user_management.""Roles"" r ON ur.""RoleId"" = r.""Id"" WHERE ur.""UserId"" = $1;", conn))
                        {
                            rc.Parameters.AddWithValue(userId);
                            await using var reader = await rc.ExecuteReaderAsync(ct);
                            while (await reader.ReadAsync(ct)) codes.Add(reader.GetString(0));
                        }
                        if (!codes.Contains("SYSTEM") && codes.Contains("ADMIN") != logtoAdmin.Value)
                        {
                            await using var tx = await conn.BeginTransactionAsync(ct);
                            await using (var del = new NpgsqlCommand(@"DELETE FROM user_management.""UserRoles"" WHERE ""UserId"" = $1 AND ""RoleId"" IN (1, 2);", conn, tx))
                            {
                                del.Parameters.AddWithValue(userId);
                                await del.ExecuteNonQueryAsync(ct);
                            }
                            await using (var ins = new NpgsqlCommand(@"
                                INSERT INTO user_management.""UserRoles"" (""UserId"", ""RoleId"", ""Id"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy"")
                                VALUES ($1, $2, $1, NOW(), 1, NOW(), 1) ON CONFLICT DO NOTHING;", conn, tx))
                            {
                                ins.Parameters.AddWithValue(userId);
                                ins.Parameters.AddWithValue(logtoAdmin.Value ? 2L : 1L);
                                await ins.ExecuteNonQueryAsync(ct);
                            }
                            await tx.CommitAsync(ct);
                            await cache.RemoveAsync($"um:permissions:user:{userId}", ct);
                            await cache.RemoveByTagAsync($"um:permissions:user:{userId}", ct);
                            memoryCache.Remove(Agent360.API.Middlewares.UserResolutionMiddleware.RoleSyncCacheKey(sub));
                            rolesChanged++;
                        }
                    }
                }

                syncedCount++;
            }

            return Results.Ok(new { success = true, synced = syncedCount, rolesChanged, total = usersArray.Count });
        })
        .WithName("AdminSyncAllUsersFromLogto")
        .WithSummary("Sync all users from Logto Management API (Admin only)");


    }
}