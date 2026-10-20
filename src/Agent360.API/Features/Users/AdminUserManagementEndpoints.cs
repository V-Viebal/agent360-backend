using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
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
        group.MapPut("{id:long}/role", async (long id, UpdateUserRoleRequest req, IConfiguration config, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

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
                        ""LogtoUserId"", ""Email"", ""FirstName"", ""LastName"", ""DisplayName"", ""Status"", ""IsDeleted"", ""CreatedAt"", ""CreatedBy"", ""ModifiedAt"", ""ModifiedBy""
                    ) VALUES (
                        $1, $2, $3, $4, $5, 1, false, NOW(), 1, NOW(), 1
                    ) RETURNING ""Id"";
                ", conn);
                insCmd.Parameters.AddWithValue(req.LogtoUserId);
                insCmd.Parameters.AddWithValue(email);
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
                updCmd.Parameters.AddWithValue(email);
                updCmd.Parameters.AddWithValue(displayName);
                updCmd.Parameters.AddWithValue(userId);
                await updCmd.ExecuteNonQueryAsync(ct);

                return Results.Ok(new { success = true, created = false, userId, logtoUserId = req.LogtoUserId });
            }
        })
        .WithName("AdminSyncUser")
        .WithSummary("Sync or upsert user into database (Admin only)");

    }
}