using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Identity;
using Npgsql;

namespace Agent360.API.Features.Agents.Assignments;

public sealed record AssignAgentRequest(long UserId, string AgentSlug, string Role = "Owner");
public sealed record SetAnyoneAccessRequest(string AgentSlug, bool Enabled, string Role = "Operator");
public sealed record AgentAssignmentDto(
    long Id,
    long UserId,
    string AgentSlug,
    string Role,
    DateTimeOffset AssignedAt,
    string? UserEmail = null,
    string? UserDisplayName = null,
    string? LogtoUserId = null);

public sealed class AgentAssignmentEndpoints : IEndpoint
{
    public const long AnyoneUserId = -1L;

    public static async Task EnsureTableExistsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            CREATE SCHEMA IF NOT EXISTS user_management;
            CREATE TABLE IF NOT EXISTS user_management.""AgentAssignments"" (
                ""Id"" bigserial PRIMARY KEY,
                ""UserId"" bigint NOT NULL,
                ""AgentSlug"" varchar(128) NOT NULL,
                ""Role"" varchar(64) NOT NULL DEFAULT Owner,
                ""AssignedAt"" timestamptz NOT NULL DEFAULT NOW(),
                ""AssignedBy"" varchar(128) NULL,
                UNIQUE (""UserId"", ""AgentSlug"")
            );
            ALTER TABLE user_management.""AgentAssignments"" DROP CONSTRAINT IF EXISTS ""AgentAssignments_UserId_fkey"";
            CREATE INDEX IF NOT EXISTS ""IX_AgentAssignments_UserId"" ON user_management.""AgentAssignments"" (""UserId"");
            CREATE INDEX IF NOT EXISTS ""IX_AgentAssignments_AgentSlug"" ON user_management.""AgentAssignments"" (LOWER(""AgentSlug""));
        ", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("admin/agents")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Admin.Agents")
            .RequireAuthorization("Admin");

        // List assignments with joined user profile details
        group.MapGet("assignments", async (IConfiguration config, CancellationToken ct) =>
        {
            var list = new List<AgentAssignmentDto>();
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Ok(list);

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTableExistsAsync(conn, ct);
            await using var cmd = new NpgsqlCommand(@"
                SELECT
                    a.""Id"",
                    a.""UserId"",
                    a.""AgentSlug"",
                    a.""Role"",
                    a.""AssignedAt"",
                    u.""Email"",
                    u.""DisplayName"",
                    u.""LogtoUserId""
                FROM user_management.""AgentAssignments"" a
                LEFT JOIN user_management.""Users"" u ON a.""UserId"" = u.""Id""
                ORDER BY a.""AssignedAt"" DESC;
            ", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AgentAssignmentDto(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return Results.Ok(list);
        })
        .WithName("ListAgentAssignments")
        .WithSummary("List all agent owner assignments (Admin only)");

        // Assign agent to user (or Anyone when UserId == -1)
        group.MapPost("assign", async (AssignAgentRequest request, CurrentUser currentUser, IConfiguration config, CancellationToken ct) =>
        {
            if ((request.UserId <= 0 && request.UserId != AnyoneUserId) || string.IsNullOrWhiteSpace(request.AgentSlug))
            {
                return Results.BadRequest(new { message = "Valid UserId (or -1 for Anyone) and AgentSlug are required." });
            }

            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database connection not configured");

            var cleanSlug = Agent360.API.Features.Agents.Security.AgentOwnershipGuard.NormalizeAgentSlug(request.AgentSlug);
            if (string.IsNullOrWhiteSpace(cleanSlug)) cleanSlug = request.AgentSlug.Trim();

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTableExistsAsync(conn, ct);
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO user_management.""AgentAssignments"" (""UserId"", ""AgentSlug"", ""Role"", ""AssignedAt"", ""AssignedBy"")
                VALUES ($1, $2, $3, NOW(), $4)
                ON CONFLICT (""UserId"", ""AgentSlug"") DO UPDATE 
                SET ""Role"" = EXCLUDED.""Role"", ""AssignedAt"" = NOW()
                RETURNING ""Id"", ""AssignedAt"";
            ", conn);

            cmd.Parameters.AddWithValue(request.UserId);
            cmd.Parameters.AddWithValue(cleanSlug);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(request.Role) ? "Owner" : request.Role.Trim());
            cmd.Parameters.AddWithValue(currentUser.ExternalUserId ?? "admin");

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var assignedAt = reader.GetFieldValue<DateTimeOffset>(1);
                await Agent360.API.Features.Agents.Security.AuditLogger.LogAsync(
                    config, currentUser.UserId, currentUser.ExternalUserId, "ASSIGN_AGENT", cleanSlug,
                    new { target_user_id = request.UserId, is_anyone = request.UserId == AnyoneUserId, role = request.Role, assignment_id = id }, ct: ct);
                return Results.Ok(new AgentAssignmentDto(id, request.UserId, cleanSlug, request.Role, assignedAt));
            }

            return Results.Problem("Failed to record assignment");
        })
        .WithName("AssignAgentToUser")
        .WithSummary("Assign an agent ownership/role to a user or Anyone (Admin only)");

        // Toggle Anyone access on an agent
        group.MapPost("anyone-access", async (SetAnyoneAccessRequest request, CurrentUser currentUser, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentSlug))
            {
                return Results.BadRequest(new { message = "AgentSlug is required." });
            }

            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database connection not configured");

            var cleanSlug = Agent360.API.Features.Agents.Security.AgentOwnershipGuard.NormalizeAgentSlug(request.AgentSlug);
            if (string.IsNullOrWhiteSpace(cleanSlug)) cleanSlug = request.AgentSlug.Trim();
            var fullSlug = $"bot:agent360:{cleanSlug}";
            var role = string.IsNullOrWhiteSpace(request.Role) ? "Operator" : request.Role.Trim();

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTableExistsAsync(conn, ct);

            if (request.Enabled)
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO user_management.""AgentAssignments"" (""UserId"", ""AgentSlug"", ""Role"", ""AssignedAt"", ""AssignedBy"")
                    VALUES ($1, $2, $3, NOW(), $4)
                    ON CONFLICT (""UserId"", ""AgentSlug"") DO UPDATE
                    SET ""Role"" = EXCLUDED.""Role"", ""AssignedAt"" = NOW()
                    RETURNING ""Id"", ""AssignedAt"";
                ", conn);
                cmd.Parameters.AddWithValue(AnyoneUserId);
                cmd.Parameters.AddWithValue(cleanSlug);
                cmd.Parameters.AddWithValue(role);
                cmd.Parameters.AddWithValue(currentUser.ExternalUserId ?? "admin");

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var id = reader.GetInt64(0);
                    var assignedAt = reader.GetFieldValue<DateTimeOffset>(1);
                    await Agent360.API.Features.Agents.Security.AuditLogger.LogAsync(
                        config, currentUser.UserId, currentUser.ExternalUserId, "SET_AGENT_ANYONE_ACCESS", cleanSlug,
                        new { enabled = true, role, assignment_id = id }, ct: ct);
                    return Results.Ok(new { success = true, enabled = true, agentSlug = cleanSlug, role, assignmentId = id, assignedAt });
                }
            }
            else
            {
                await using var cmd = new NpgsqlCommand(@"
                    DELETE FROM user_management.""AgentAssignments""
                    WHERE ""UserId"" = $1 AND (LOWER(""AgentSlug"") = $2 OR LOWER(""AgentSlug"") = $3);
                ", conn);
                cmd.Parameters.AddWithValue(AnyoneUserId);
                cmd.Parameters.AddWithValue(cleanSlug.ToLowerInvariant());
                cmd.Parameters.AddWithValue(fullSlug.ToLowerInvariant());
                await cmd.ExecuteNonQueryAsync(ct);

                await Agent360.API.Features.Agents.Security.AuditLogger.LogAsync(
                    config, currentUser.UserId, currentUser.ExternalUserId, "SET_AGENT_ANYONE_ACCESS", cleanSlug,
                    new { enabled = false }, ct: ct);
                return Results.Ok(new { success = true, enabled = false, agentSlug = cleanSlug });
            }

            return Results.Problem("Failed to update Anyone access");
        })
        .WithName("SetAgentAnyoneAccess")
        .WithSummary("Enable or disable Anyone (all authenticated users) access on an agent (Admin only)");

        // Remove assignment
        group.MapDelete("assignments/{id:long}", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database connection not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTableExistsAsync(conn, ct);
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM user_management.\"AgentAssignments\" WHERE \"Id\" = $1", 
                conn);
            cmd.Parameters.AddWithValue(id);
            var rows = await cmd.ExecuteNonQueryAsync(ct);

            if (rows > 0)
            {
                await Agent360.API.Features.Agents.Security.AuditLogger.LogAsync(
                    config, null, "admin", "REMOVE_AGENT_ASSIGNMENT", id.ToString(), new { assignment_id = id }, ct: ct);
            }
            return rows > 0 ? Results.Ok(new { success = true }) : Results.NotFound();
        })
        .WithName("RemoveAgentAssignment")
        .WithSummary("Remove an agent assignment (Admin only)");
    }
}
