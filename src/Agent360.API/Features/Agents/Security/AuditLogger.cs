using Npgsql;
using System.Text.Json;

namespace Agent360.API.Features.Agents.Security;

public static class AuditLogger
{
    public static async Task LogAsync(
        IConfiguration config,
        long? actorId,
        string? actorExternalId,
        string action,
        string targetResource,
        object details,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var connStr = config.GetConnectionString("DefaultConnection") 
            ?? config.GetConnectionString("Postgres");

        if (string.IsNullOrWhiteSpace(connStr)) return;

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var sql = @"
                INSERT INTO user_management.""AuditLogs"" 
                (""ActorId"", ""ActorExternalId"", ""Action"", ""TargetResource"", ""Details"", ""IpAddress"", ""CreatedAt"")
                VALUES ($1, $2, $3, $4, $5::jsonb, $6, NOW());
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(actorId.HasValue ? actorId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(actorExternalId ?? "system");
            cmd.Parameters.AddWithValue(action);
            cmd.Parameters.AddWithValue(targetResource);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(details));
            cmd.Parameters.AddWithValue(ipAddress ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Fail open for audit writing
        }
    }
}
