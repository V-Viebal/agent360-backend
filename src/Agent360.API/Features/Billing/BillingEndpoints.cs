using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Identity;
using Npgsql;

namespace Agent360.API.Features.Billing;

public sealed record TopupRequest(string PackageId);

public sealed class BillingEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("billing")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Billing");

        // 1. Get Wallet status & Topup Packages (Same pattern as App360)
        group.MapGet("wallet", async (CurrentUser currentUser, IConfiguration config, CancellationToken ct) =>
        {
            var tenantId = currentUser.ExternalUserId ?? "default";
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            // Ensure wallet exists
            await using (var insCmd = new NpgsqlCommand(@"
                INSERT INTO user_management.""Wallets"" (""TenantId"", ""BalanceTokens"", ""Status"", ""UpdatedAt"")
                VALUES ($1, 500000.0000, 'Active', NOW())
                ON CONFLICT (""TenantId"") DO NOTHING;
            ", conn))
            {
                insCmd.Parameters.AddWithValue(tenantId);
                await insCmd.ExecuteNonQueryAsync(ct);
            }

            decimal balance = 0;
            string status = "green";
            await using (var qCmd = new NpgsqlCommand(@"
                SELECT ""BalanceTokens"", ""Status"" FROM user_management.""Wallets"" WHERE ""TenantId"" = $1;
            ", conn))
            {
                qCmd.Parameters.AddWithValue(tenantId);
                await using var reader = await qCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    balance = reader.GetDecimal(0);
                    status = reader.GetString(1);
                }
            }

            // Fetch active topup packages
            var packages = new List<object>();
            await using (var pkgCmd = new NpgsqlCommand(@"
                SELECT ""Id"", ""Name"", ""FiatPrice"", ""TokenYield"" 
                FROM user_management.""TopupPackages"" 
                WHERE ""IsActive"" = true 
                ORDER BY ""FiatPrice"" ASC;
            ", conn))
            {
                await using var reader = await pkgCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    packages.Add(new
                    {
                        id = reader.GetString(0),
                        name = reader.GetString(1),
                        fiat_price = reader.GetDecimal(2),
                        token_yield = reader.GetDecimal(3)
                    });
                }
            }

            var dailyBurn = 25000;
            var runwayDays = dailyBurn > 0 ? (int)(balance / dailyBurn) : 999;

            return Results.Ok(new
            {
                tenant_id = tenantId,
                balance_tokens = balance,
                daily_burn = dailyBurn,
                runway_days = runwayDays,
                status = balance > 50000 ? "green" : balance > 10000 ? "yellow" : "red",
                packages
            });
        })
        .WithName("GetWalletBilling")
        .WithSummary("Get user token wallet and PayOS topup packages")
        .RequireAuthorization();

        // 2. Initialize PayOS Topup Checkout (Pattern matching App360)
        group.MapPost("topup", async (TopupRequest request, CurrentUser currentUser, PayOSService payOS, IConfiguration config, CancellationToken ct) =>
        {
            var tenantId = currentUser.ExternalUserId ?? "default";
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            // Fetch package details
            string? pkgName = null;
            decimal fiatPrice = 0;
            decimal tokenYield = 0;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT ""Name"", ""FiatPrice"", ""TokenYield"" 
                FROM user_management.""TopupPackages"" 
                WHERE ""Id"" = $1 AND ""IsActive"" = true;
            ", conn))
            {
                cmd.Parameters.AddWithValue(request.PackageId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    pkgName = reader.GetString(0);
                    fiatPrice = reader.GetDecimal(1);
                    tokenYield = reader.GetDecimal(2);
                }
            }

            if (pkgName is null) return Results.BadRequest(new { message = "Invalid topup package" });

            var orderCode = long.Parse($"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{Random.Shared.Next(100, 999)}");
            var amount = (int)fiatPrice;
            var returnUrl = "https://agent360.antares.vn/admin/billings?status=TOPUP_PAID&orderCode=" + orderCode;
            var cancelUrl = "https://agent360.antares.vn/admin/billings?status=CANCELLED&orderCode=" + orderCode;

            var (checkoutUrl, linkId, err) = await payOS.CreatePaymentLinkAsync(
                orderCode,
                amount,
                $"Topup {tokenYield:N0}".Substring(0, Math.Min(25, $"Topup {tokenYield:N0}".Length)),
                returnUrl,
                cancelUrl,
                ct);

            if (checkoutUrl is null)
            {
                return Results.Problem(err ?? "Failed to create checkout link with PayOS");
            }

            // Save transaction record
            await using (var txCmd = new NpgsqlCommand(@"
                INSERT INTO user_management.""PaymentTransactions"" 
                (""TenantId"", ""OrderCode"", ""Amount"", ""Status"", ""PaymentLinkId"", ""CheckoutUrl"", ""PackageId"", ""CreatedAt"")
                VALUES ($1, $2, $3, 'pending', $4, $5, $6, NOW());
            ", conn))
            {
                txCmd.Parameters.AddWithValue(tenantId);
                txCmd.Parameters.AddWithValue(orderCode);
                txCmd.Parameters.AddWithValue(fiatPrice);
                txCmd.Parameters.AddWithValue(linkId ?? (object)DBNull.Value);
                txCmd.Parameters.AddWithValue(checkoutUrl);
                txCmd.Parameters.AddWithValue(request.PackageId);
                await txCmd.ExecuteNonQueryAsync(ct);
            }

            return Results.Ok(new
            {
                url = checkoutUrl,
                orderCode,
                amount
            });
        })
        .WithName("CreateTopupCheckout")
        .WithSummary("Create PayOS payment link for token wallet topup")
        .RequireAuthorization();

        // 3. Payment Sync & Verification Endpoint
        group.MapPost("sync-order", async (long orderCode, CurrentUser currentUser, IConfiguration config, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            // Fetch transaction
            string? tenantId = null;
            string? pkgId = null;
            string status = "pending";
            await using (var cmd = new NpgsqlCommand(@"
                SELECT ""TenantId"", ""PackageId"", ""Status"" 
                FROM user_management.""PaymentTransactions"" 
                WHERE ""OrderCode"" = $1;
            ", conn))
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

            if (tenantId is null) return Results.NotFound(new { message = "Transaction not found" });

            if (status != "paid" && status != "success")
            {
                // Mark paid and credit tokens
                await using (var creditCmd = new NpgsqlCommand(@"
                    UPDATE user_management.""PaymentTransactions""
                    SET ""Status"" = 'paid', ""CompletedAt"" = NOW()
                    WHERE ""OrderCode"" = $1;

                    UPDATE user_management.""Wallets""
                    SET ""BalanceTokens"" = ""BalanceTokens"" + (
                        SELECT ""TokenYield"" FROM user_management.""TopupPackages"" WHERE ""Id"" = $2
                    ),
                    ""UpdatedAt"" = NOW()
                    WHERE ""TenantId"" = $3;
                ", conn))
                {
                    creditCmd.Parameters.AddWithValue(orderCode);
                    creditCmd.Parameters.AddWithValue(pkgId!);
                    creditCmd.Parameters.AddWithValue(tenantId);
                    await creditCmd.ExecuteNonQueryAsync(ct);
                }
            }

            return Results.Ok(new { success = true, orderCode, status = "paid" });
        })
        .WithName("SyncPaymentOrder")
        .WithSummary("Confirm and credit PayOS token order")
        .AllowAnonymous();
    }
}
