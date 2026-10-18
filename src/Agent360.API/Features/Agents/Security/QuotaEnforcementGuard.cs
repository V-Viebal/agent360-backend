using Npgsql;

namespace Agent360.API.Features.Agents.Security;

public static class QuotaEnforcementGuard
{
    public static async Task<(bool Allowed, string? Reason)> CheckAndConsumeQuotaAsync(
        long userId,
        int estimatedTokens,
        IConfiguration config,
        CancellationToken ct = default)
    {
        if (userId <= 0) return (true, null);

        var connStr = config.GetConnectionString("DefaultConnection") 
            ?? config.GetConnectionString("Postgres");

        if (string.IsNullOrWhiteSpace(connStr)) return (true, null);

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var sql = @"
                INSERT INTO user_management.""UserQuotas"" (""UserId"", ""DailyTokenLimit"", ""DailyTokensUsed"", ""LastResetDate"")
                VALUES ($1, 200000, 0, CURRENT_DATE)
                ON CONFLICT (""UserId"") DO UPDATE SET
                    ""DailyTokensUsed"" = CASE 
                        WHEN user_management.""UserQuotas"".""LastResetDate"" < CURRENT_DATE THEN 0 
                        ELSE user_management.""UserQuotas"".""DailyTokensUsed"" 
                    END,
                    ""LastResetDate"" = CURRENT_DATE
                RETURNING ""DailyTokenLimit"", ""DailyTokensUsed"";
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(userId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var limit = reader.GetInt32(0);
                var used = reader.GetInt32(1);

                if (used + estimatedTokens > limit)
                {
                    return (false, $"Daily token quota exceeded ({used}/{limit} tokens used). Quota resets at 00:00 UTC.");
                }
            }
            await reader.CloseAsync();

            // Record consumption
            await using var updateCmd = new NpgsqlCommand(@"
                UPDATE user_management.""UserQuotas"" 
                SET ""DailyTokensUsed"" = ""DailyTokensUsed"" + $2 
                WHERE ""UserId"" = $1;
            ", conn);
            updateCmd.Parameters.AddWithValue(userId);
            updateCmd.Parameters.AddWithValue(estimatedTokens);
            await updateCmd.ExecuteNonQueryAsync(ct);

            return (true, null);
        }
        catch
        {
            return (true, null);
        }
    }
}
