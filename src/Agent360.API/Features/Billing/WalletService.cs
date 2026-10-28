using System.Net.Mail;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Agent360.API.Features.Billing;

public sealed class WalletService(
    IConfiguration config,
    IMemoryCache cache,
    ILogger<WalletService> logger)
{
    private string? ConnectionString =>
        config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");

    /// <summary>
    /// Checks if tenant has sufficient balance to execute inference turn
    /// </summary>
    public async Task<(bool Allowed, decimal Balance, string? Reason)> AssertSufficientBalanceAsync(
        string tenantId,
        decimal requiredTokens = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return (true, 500000, null);

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                SELECT ""BalanceTokens"" 
                FROM user_management.""Wallets"" 
                WHERE ""TenantId"" = $1;
            ", conn);
            cmd.Parameters.AddWithValue(tenantId);

            var obj = await cmd.ExecuteScalarAsync(ct);
            decimal balance = 500000;
            if (obj != null && obj != DBNull.Value)
            {
                balance = Convert.ToDecimal(obj);
            }

            if (balance < requiredTokens)
            {
                return (false, balance, $"Số dư ví token của bạn ({balance:#,##0} tokens) không đủ để thực hiện yêu cầu ({requiredTokens:#,##0} tokens). Vui lòng nạp thêm token.");
            }

            return (true, balance, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check wallet balance for {TenantId}, allowing fail-open for availability", tenantId);
            return (true, 500000, null);
        }
    }

    /// <summary>
    /// Transactionally deducts tokens from wallet and records ledger entry
    /// </summary>
    public async Task<decimal> DeductTokensAsync(
        string tenantId,
        decimal tokens,
        string type,
        string description,
        string? referenceId = null,
        CancellationToken ct = default)
    {
        if (tokens <= 0 || string.IsNullOrWhiteSpace(ConnectionString)) return 0;

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            decimal currentBalance = 0;
            await using (var lockCmd = new NpgsqlCommand(@"
                SELECT ""BalanceTokens"" 
                FROM user_management.""Wallets"" 
                WHERE ""TenantId"" = $1 
                FOR UPDATE;
            ", conn, tx))
            {
                lockCmd.Parameters.AddWithValue(tenantId);
                var obj = await lockCmd.ExecuteScalarAsync(ct);
                if (obj != null && obj != DBNull.Value)
                {
                    currentBalance = Convert.ToDecimal(obj);
                }
            }

            var newBalance = Math.Max(0, currentBalance - tokens);

            await using (var updateCmd = new NpgsqlCommand(@"
                UPDATE user_management.""Wallets""
                SET ""BalanceTokens"" = $1, ""UpdatedAt"" = NOW()
                WHERE ""TenantId"" = $2;
            ", conn, tx))
            {
                updateCmd.Parameters.AddWithValue(newBalance);
                updateCmd.Parameters.AddWithValue(tenantId);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var ledgerCmd = new NpgsqlCommand(@"
                INSERT INTO user_management.""LedgerTransactions""
                (""TenantId"", ""Amount"", ""BalanceAfter"", ""Type"", ""ReferenceId"", ""Description"", ""CreatedAt"")
                VALUES ($1, $2, $3, $4, $5, $6, NOW());
            ", conn, tx))
            {
                ledgerCmd.Parameters.AddWithValue(tenantId);
                ledgerCmd.Parameters.AddWithValue(-tokens);
                ledgerCmd.Parameters.AddWithValue(newBalance);
                ledgerCmd.Parameters.AddWithValue(type);
                ledgerCmd.Parameters.AddWithValue(referenceId ?? (object)DBNull.Value);
                ledgerCmd.Parameters.AddWithValue(description);
                await ledgerCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            // Check for low balance alert
            if (newBalance <= 25000)
            {
                _ = TriggerLowBalanceAlertAsync(tenantId, newBalance);
            }

            return newBalance;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deduct {Tokens} tokens from tenant {TenantId}", tokens, tenantId);
            return 0;
        }
    }

    /// <summary>
    /// Transactionally grants/adjusts tokens (for Admin bonus, refund, or adjustments)
    /// </summary>
    public async Task<decimal> AdjustTokensAsync(
        string tenantId,
        decimal tokens,
        string type,
        string description,
        string? referenceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return 0;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);

        decimal currentBalance = 0;
        await using (var lockCmd = new NpgsqlCommand(@"
            SELECT ""BalanceTokens"" 
            FROM user_management.""Wallets"" 
            WHERE ""TenantId"" = $1 
            FOR UPDATE;
        ", conn, tx))
        {
            lockCmd.Parameters.AddWithValue(tenantId);
            var obj = await lockCmd.ExecuteScalarAsync(ct);
            if (obj != null && obj != DBNull.Value)
            {
                currentBalance = Convert.ToDecimal(obj);
            }
        }

        var newBalance = Math.Max(0, currentBalance + tokens);

        await using (var updateCmd = new NpgsqlCommand(@"
            UPDATE user_management.""Wallets""
            SET ""BalanceTokens"" = $1, ""UpdatedAt"" = NOW()
            WHERE ""TenantId"" = $2;
        ", conn, tx))
        {
            updateCmd.Parameters.AddWithValue(newBalance);
            updateCmd.Parameters.AddWithValue(tenantId);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var ledgerCmd = new NpgsqlCommand(@"
            INSERT INTO user_management.""LedgerTransactions""
            (""TenantId"", ""Amount"", ""BalanceAfter"", ""Type"", ""ReferenceId"", ""Description"", ""CreatedAt"")
            VALUES ($1, $2, $3, $4, $5, $6, NOW());
        ", conn, tx))
        {
            ledgerCmd.Parameters.AddWithValue(tenantId);
            ledgerCmd.Parameters.AddWithValue(tokens);
            ledgerCmd.Parameters.AddWithValue(newBalance);
            ledgerCmd.Parameters.AddWithValue(type);
            ledgerCmd.Parameters.AddWithValue(referenceId ?? (object)DBNull.Value);
            ledgerCmd.Parameters.AddWithValue(description);
            await ledgerCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return newBalance;
    }

    private async Task TriggerLowBalanceAlertAsync(string tenantId, decimal balance)
    {
        var cacheKey = $"low_balance_alert:{tenantId}";
        if (cache.TryGetValue(cacheKey, out _)) return; // Don't spam, 1 alert per 12 hours

        cache.Set(cacheKey, true, TimeSpan.FromHours(12));

        try
        {
            var host = config["SMTP_HOST"] ?? "smtp.resend.com";
            var port = int.TryParse(config["SMTP_PORT"], out var p) ? p : 465;
            var user = config["SMTP_USER"] ?? "resend";
            var pass = config["SMTP_PASSWORD"] ?? "";
            var sender = config["SENDER_EMAIL"] ?? "noreply@hmz.one";
            var recipient = config["ALERT_EMAIL"] ?? "hmzonehub@gmail.com";

            using var smtp = new SmtpClient(host, port)
            {
                EnableSsl = true,
                Credentials = new System.Net.NetworkCredential(user, pass),
                Timeout = 10000
            };

            using var mail = new MailMessage
            {
                From = new MailAddress(sender, "Agent360 Billing Guard"),
                Subject = $"[Cảnh Báo] Số Dư Ví Token Sắp Hết - Tenant {tenantId}",
                Body = $@"
Xin chào Admin,

Ví token của tài khoản '{tenantId}' trên nền tảng Agent360 hiện chỉ còn: {balance:#,##0} Tokens (dự báo Runway dưới 3 ngày).

Để tránh tình trạng các Agent tự động bị gián đoạn khi thực thi tác vụ, vui lòng thực hiện nạp thêm token tại:
https://skyer.vn/admin/billings

Trân trọng,
Agent360 Autonomous Fleet Management
",
                IsBodyHtml = false
            };
            mail.To.Add(recipient);

            await smtp.SendMailAsync(mail);
            logger.LogInformation("Low balance email alert sent for tenant {TenantId} (balance: {Balance})", tenantId, balance);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send low balance email alert for tenant {TenantId}", tenantId);
        }
    }
}
