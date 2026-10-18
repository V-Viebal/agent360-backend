using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
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

                        await using (var cmd = new NpgsqlCommand(@"
                            SELECT ""TenantId"", ""PackageId"", ""Status"" 
                            FROM user_management.""PaymentTransactions"" 
                            WHERE ""OrderCode"" = $1 
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
                            }
                        }

                        if (tenantId != null && status != "paid" && status != "success")
                        {
                            await using (var updateCmd = new NpgsqlCommand(@"
                                UPDATE user_management.""PaymentTransactions""
                                SET ""Status"" = 'paid', ""CompletedAt"" = NOW()
                                WHERE ""OrderCode"" = $1;

                                UPDATE user_management.""Wallets""
                                SET ""BalanceTokens"" = ""BalanceTokens"" + (
                                    SELECT ""TokenYield"" FROM user_management.""TopupPackages"" WHERE ""Id"" = $2
                                ),
                                ""UpdatedAt"" = NOW()
                                WHERE ""TenantId"" = $3;
                            ", conn, tx))
                            {
                                updateCmd.Parameters.AddWithValue(orderCode);
                                updateCmd.Parameters.AddWithValue(pkgId!);
                                updateCmd.Parameters.AddWithValue(tenantId);
                                await updateCmd.ExecuteNonQueryAsync(ct);
                            }

                            await tx.CommitAsync(ct);
                            logger.LogInformation("Successfully credited tokens for order {OrderCode} to tenant {TenantId}", orderCode, tenantId);
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
