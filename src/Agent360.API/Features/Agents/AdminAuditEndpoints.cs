using Microsoft.AspNetCore.Mvc;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Npgsql;
using System.Text.Json;

namespace Agent360.API.Features.Agents;

public sealed record AuditLogEntryDto(
    long Id,
    long? ActorId,
    string? ActorExternalId,
    string Action,
    string TargetResource,
    JsonElement Details,
    string? IpAddress,
    DateTimeOffset CreatedAt);

public sealed record UserQuotaDto(
    long UserId,
    int DailyTokenLimit,
    int DailyTokensUsed,
    int RemainingTokens,
    string LastResetDate);

public sealed record UpdateUserQuotaRequest(int DailyTokenLimit);

public sealed class AdminAuditEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("admin/audit")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Admin.Audit")
            .RequireAuthorization("Admin");

        // List audit trails
        group.MapGet("logs", async (IConfiguration config, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        {
            var logs = new List<AuditLogEntryDto>();
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Ok(logs);

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var sql = @"
                SELECT ""Id"", ""ActorId"", ""ActorExternalId"", ""Action"", ""TargetResource"", ""Details"", ""IpAddress"", ""CreatedAt""
                FROM user_management.""AuditLogs""
                ORDER BY ""CreatedAt"" DESC
                LIMIT $1;
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(Math.Clamp(limit, 1, 200));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var actorId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var actorExtId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var action = reader.GetString(3);
                var target = reader.GetString(4);
                var detailsRaw = reader.GetString(5);
                var ip = reader.IsDBNull(6) ? null : reader.GetString(6);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(7);

                var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(detailsRaw) ? "{}" : detailsRaw).RootElement.Clone();
                logs.Add(new AuditLogEntryDto(id, actorId, actorExtId, action, target, doc, ip, createdAt));
            }

            return Results.Ok(logs);
        })
        .WithName("AdminGetAuditLogs")
        .WithSummary("Get security audit logs (Admin only)");

                // Composio Webhook Connection Deliveries Audit Log
        group.MapGet("composio-connections", async (IConfiguration config, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        {
            var logs = new List<AuditLogEntryDto>();
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Ok(logs);

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var sql = @"
                SELECT ""Id"", ""ActorId"", ""ActorExternalId"", ""Action"", ""TargetResource"", ""Details"", ""IpAddress"", ""CreatedAt""
                FROM user_management.""AuditLogs""
                WHERE ""Action"" LIKE 'composio.connection%' OR ""ActorExternalId"" = 'composio:webhook'
                ORDER BY ""CreatedAt"" DESC
                LIMIT $1;
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(Math.Clamp(limit, 1, 200));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var actorId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var actorExtId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var action = reader.GetString(3);
                var target = reader.GetString(4);
                var detailsRaw = reader.GetString(5);
                var ip = reader.IsDBNull(6) ? null : reader.GetString(6);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(7);

                var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(detailsRaw) ? "{}" : detailsRaw).RootElement.Clone();
                logs.Add(new AuditLogEntryDto(id, actorId, actorExtId, action, target, doc, ip, createdAt));
            }

            return Results.Ok(logs);
        })
        .WithName("AdminGetComposioConnectionAuditLogs")
        .WithSummary("Get Composio webhook connection delivery audit trails (Admin only)");

        // List & update user quotas
        group.MapGet("quotas", async (IConfiguration config, CancellationToken ct) =>
        {
            var quotas = new List<UserQuotaDto>();
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Ok(quotas);

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var sql = @"
                SELECT u.""Id"", COALESCE(q.""DailyTokenLimit"", 200000), COALESCE(q.""DailyTokensUsed"", 0), COALESCE(q.""LastResetDate""::text, CURRENT_DATE::text)
                FROM user_management.""Users"" u
                LEFT JOIN user_management.""UserQuotas"" q ON u.""Id"" = q.""UserId""
                WHERE u.""IsDeleted"" = false
                ORDER BY u.""Id"" ASC;
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var uId = reader.GetInt64(0);
                var limit = reader.GetInt32(1);
                var used = reader.GetInt32(2);
                var date = reader.GetString(3);
                quotas.Add(new UserQuotaDto(uId, limit, used, Math.Max(0, limit - used), date));
            }

            return Results.Ok(quotas);
        })
        .WithName("AdminGetUserQuotas")
        .WithSummary("Get user token quotas (Admin only)");

        group.MapPut("quotas/{userId:long}", async (long userId, UpdateUserQuotaRequest req, IConfiguration config, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO user_management.""UserQuotas"" (""UserId"", ""DailyTokenLimit"", ""DailyTokensUsed"", ""LastResetDate"")
                VALUES ($1, $2, 0, CURRENT_DATE)
                ON CONFLICT (""UserId"") DO UPDATE SET ""DailyTokenLimit"" = EXCLUDED.""DailyTokenLimit"";
            ", conn);

            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(Math.Max(1000, req.DailyTokenLimit));

            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { success = true, userId, dailyTokenLimit = req.DailyTokenLimit });
        })
        .WithName("AdminUpdateUserQuota")
        .WithSummary("Update a user's daily token quota (Admin only)");
    }
}
