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
    }
}
