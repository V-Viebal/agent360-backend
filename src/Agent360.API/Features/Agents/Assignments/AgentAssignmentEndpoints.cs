using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Identity;
using Npgsql;

namespace Agent360.API.Features.Agents.Assignments;

public sealed record AssignAgentRequest(long UserId, string AgentSlug, string Role = "Owner");
public sealed record AgentAssignmentDto(long Id, long UserId, string AgentSlug, string Role, DateTimeOffset AssignedAt);

public sealed class AgentAssignmentEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("admin/agents")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Admin.Agents")
            .RequireAuthorization("Admin");

        // List assignments
        group.MapGet("assignments", async (IConfiguration config, CancellationToken ct) =>
        {
            var list = new List<AgentAssignmentDto>();
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Ok(list);

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT \"Id\", \"UserId\", \"AgentSlug\", \"Role\", \"AssignedAt\" FROM user_management.\"AgentAssignments\" ORDER BY \"AssignedAt\" DESC",
                conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AgentAssignmentDto(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }

            return Results.Ok(list);
        })
        .WithName("ListAgentAssignments")
        .WithSummary("List all agent owner assignments (Admin only)");

        // Assign agent to user
        group.MapPost("assign", async (AssignAgentRequest request, CurrentUser currentUser, IConfiguration config, CancellationToken ct) =>
        {
            if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.AgentSlug))
            {
                return Results.BadRequest(new { message = "UserId and AgentSlug are required." });
            }

            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database connection not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO user_management.""AgentAssignments"" (""UserId"", ""AgentSlug"", ""Role"", ""AssignedAt"", ""AssignedBy"")
                VALUES ($1, $2, $3, NOW(), $4)
                ON CONFLICT (""UserId"", ""AgentSlug"") DO UPDATE 
                SET ""Role"" = EXCLUDED.""Role"", ""AssignedAt"" = NOW()
                RETURNING ""Id"", ""AssignedAt"";
            ", conn);

            cmd.Parameters.AddWithValue(request.UserId);
            cmd.Parameters.AddWithValue(request.AgentSlug.Trim());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(request.Role) ? "Owner" : request.Role.Trim());
            cmd.Parameters.AddWithValue(currentUser.ExternalUserId ?? "admin");

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var assignedAt = reader.GetFieldValue<DateTimeOffset>(1);
                return Results.Ok(new AgentAssignmentDto(id, request.UserId, request.AgentSlug, request.Role, assignedAt));
            }

            return Results.Problem("Failed to record assignment");
        })
        .WithName("AssignAgentToUser")
        .WithSummary("Assign an agent ownership/role to a user (Admin only)");

        // Remove assignment
        group.MapDelete("assignments/{id:long}", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database connection not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM user_management.\"AgentAssignments\" WHERE \"Id\" = $1", 
                conn);
            cmd.Parameters.AddWithValue(id);
            var rows = await cmd.ExecuteNonQueryAsync(ct);

            return rows > 0 ? Results.Ok(new { success = true }) : Results.NotFound();
        })
        .WithName("RemoveAgentAssignment")
        .WithSummary("Remove an agent assignment (Admin only)");
    }
}
