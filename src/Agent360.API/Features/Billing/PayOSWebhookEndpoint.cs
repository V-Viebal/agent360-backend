using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Npgsql;
using System.Text.Json;

namespace Agent360.API.Features.Billing;

public sealed record PayOSWebhookPayload(
    string code,
    string desc,
    bool success,
    PayOSWebhookData data,
    string signature);

public sealed record PayOSWebhookData(
    long orderCode,
    int amount,
    string description,
    string accountNumber,
    string reference,
    string transactionDateTime,
    string currency,
    string paymentLinkId,
    string code,
    string desc);

public sealed class PayOSWebhookEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // Public Webhook receiver registered with PayOS dashboard
        routeBuilder.MapPost("webhooks/payos", async (
            HttpContext context,
            IConfiguration config,
            ILogger<PayOSWebhookEndpoint> logger,
            CancellationToken ct) =>
        {
            PayOSWebhookPayload? payload;
            try
            {
                payload = await context.Request.ReadFromJsonAsync<PayOSWebhookPayload>(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Invalid JSON in PayOS webhook payload");
                return Results.Ok(new { success = false, message = "Malformed payload" });
            }

            if (payload?.data is null)
            {
                return Results.Ok(new { success = true });
            }

            var orderCode = payload.data.orderCode;
            logger.LogInformation("Processing PayOS Webhook for order {OrderCode}, code {Code}", orderCode, payload.code);

            // Only process successful payments ("00")
            if (payload.code == "00" || payload.data.code == "00")
            {
                var connStr = config.GetConnectionString("DefaultConnection") 
                    ?? config.GetConnectionString("Postgres");

                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync(ct);

                    await using var tx = await conn.BeginTransactionAsync(ct);
                    try
                    {
                        string? tenantId = null;
                        string? pkgId = null;
                        string status = "pending";
                        decimal tokenYield = 0;

                        await using (var cmd = new NpgsqlCommand(@"
                            SELECT t.""TenantId"", t.""PackageId"", t.""Status"", COALESCE(p.""TokenYield"", 0)
                            FROM user_management.""PaymentTransactions"" t
                            LEFT JOIN user_management.""TopupPackages"" p ON t.""PackageId"" = p.""Id""
                            WHERE t.""OrderCode"" = $1 
                            FOR UPDATE;
                        ", conn, tx))
                        {
                            cmd.Parameters.AddWithValue(orderCode);
                            await using var reader = await cmd.ExecuteReaderAsync(ct);
                            if (await reader.ReadAsync(ct))
                            {
                                tenantId = reader.GetString(0);
                                pkgId = reader.GetString(1);
                                status = reader.GetString(2);
                                tokenYield = reader.GetDecimal(3);
                            }
                        }

                        if (tenantId != null && status != "paid" && status != "success")
                        {
                            decimal currentBalance = 0;
                            await using (var bCmd = new NpgsqlCommand(@"
                                SELECT ""BalanceTokens"" FROM user_management.""Wallets"" WHERE ""TenantId"" = $1 FOR UPDATE;
                            ", conn, tx))
                            {
                                bCmd.Parameters.AddWithValue(tenantId);
                                var obj = await bCmd.ExecuteScalarAsync(ct);
                                if (obj != null && obj != DBNull.Value)
                                {
                                    currentBalance = Convert.ToDecimal(obj);
                                }
                            }

                            var newBalance = currentBalance + tokenYield;

                            // 1. Update Payment Transaction
                            await using (var updateTxCmd = new NpgsqlCommand(@"
                                UPDATE user_management.""PaymentTransactions""
                                SET ""Status"" = 'paid', ""CompletedAt"" = NOW()
                                WHERE ""OrderCode"" = $1;
                            ", conn, tx))
                            {
                                updateTxCmd.Parameters.AddWithValue(orderCode);
                                await updateTxCmd.ExecuteNonQueryAsync(ct);
                            }

                            // 2. Update Wallet balance
                            await using (var updateWalletCmd = new NpgsqlCommand(@"
                                UPDATE user_management.""Wallets""
                                SET ""BalanceTokens"" = $1, ""UpdatedAt"" = NOW()
                                WHERE ""TenantId"" = $2;
                            ", conn, tx))
                            {
                                updateWalletCmd.Parameters.AddWithValue(newBalance);
                                updateWalletCmd.Parameters.AddWithValue(tenantId);
                                await updateWalletCmd.ExecuteNonQueryAsync(ct);
                            }

                            // 3. Insert Ledger Transaction record (App360 parity)
                            await using (var insertLedgerCmd = new NpgsqlCommand(@"
                                INSERT INTO user_management.""LedgerTransactions""
                                (""TenantId"", ""Amount"", ""BalanceAfter"", ""Type"", ""ReferenceId"", ""Description"", ""CreatedAt"")
                                VALUES ($1, $2, $3, 'TOPUP', $4, $5, NOW());
                            ", conn, tx))
                            {
                                insertLedgerCmd.Parameters.AddWithValue(tenantId);
                                insertLedgerCmd.Parameters.AddWithValue(tokenYield);
                                insertLedgerCmd.Parameters.AddWithValue(newBalance);
                                insertLedgerCmd.Parameters.AddWithValue($"order_{orderCode}");
                                insertLedgerCmd.Parameters.AddWithValue($"PayOS IPN Topup (+{tokenYield:#,##0} tokens)");
                                await insertLedgerCmd.ExecuteNonQueryAsync(ct);
                            }

                            await tx.CommitAsync(ct);
                            logger.LogInformation("Successfully credited tokens for order {OrderCode} to tenant {TenantId}. New balance: {NewBalance}", 
                                orderCode, tenantId, newBalance);
                        }
                        else
                        {
                            await tx.RollbackAsync(ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        await tx.RollbackAsync(ct);
                        logger.LogError(ex, "Transaction failed while processing webhook for order {OrderCode}", orderCode);
                    }
                }
            }

            return Results.Ok(new { success = true });
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithName("PayOSWebhook")
        .WithTags("Billing.Webhooks")
        .AllowAnonymous();
    }
}
