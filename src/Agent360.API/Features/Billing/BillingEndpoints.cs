using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Identity;
using Npgsql;
using System.Text.Json;

namespace Agent360.API.Features.Billing;

public sealed record TopupRequest(string PackageId, bool? Mock = null);

public sealed record LedgerTransactionDto(
    long Id,
    string TenantId,
    decimal Amount,
    decimal BalanceAfter,
    string Type,
    string? ReferenceId,
    string Description,
    DateTimeOffset CreatedAt);

public sealed class BillingEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("billing")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Billing");

        // Ensure database tables exist helper
        async Task EnsureTablesAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(@"
                CREATE SCHEMA IF NOT EXISTS user_management;

                CREATE TABLE IF NOT EXISTS user_management.""Wallets"" (
                    ""TenantId"" text PRIMARY KEY,
                    ""BalanceTokens"" numeric(18, 4) NOT NULL DEFAULT 500000.0000,
                    ""Status"" text NOT NULL DEFAULT 'Active',
                    ""UpdatedAt"" timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS user_management.""TopupPackages"" (
                    ""Id"" text PRIMARY KEY,
                    ""Name"" text NOT NULL,
                    ""FiatPrice"" numeric(18, 2) NOT NULL,
                    ""TokenYield"" numeric(18, 4) NOT NULL,
                    ""IsActive"" boolean NOT NULL DEFAULT true
                );

                CREATE TABLE IF NOT EXISTS user_management.""PaymentTransactions"" (
                    ""Id"" bigserial PRIMARY KEY,
                    ""TenantId"" text NOT NULL,
                    ""OrderCode"" bigint NOT NULL UNIQUE,
                    ""Amount"" numeric(18, 2) NOT NULL,
                    ""Status"" text NOT NULL DEFAULT 'pending',
                    ""PaymentLinkId"" text,
                    ""CheckoutUrl"" text,
                    ""PackageId"" text,
                    ""CreatedAt"" timestamptz NOT NULL DEFAULT NOW(),
                    ""CompletedAt"" timestamptz
                );

                CREATE TABLE IF NOT EXISTS user_management.""LedgerTransactions"" (
                    ""Id"" bigserial PRIMARY KEY,
                    ""TenantId"" text NOT NULL,
                    ""Amount"" numeric(18, 4) NOT NULL,
                    ""BalanceAfter"" numeric(18, 4) NOT NULL,
                    ""Type"" text NOT NULL,
                    ""ReferenceId"" text,
                    ""Description"" text NOT NULL,
                    ""CreatedAt"" timestamptz NOT NULL DEFAULT NOW()
                );

                INSERT INTO user_management.""TopupPackages"" (""Id"", ""Name"", ""FiatPrice"", ""TokenYield"", ""IsActive"")
                VALUES 
                    ('pkg_starter', 'Starter Pack (1M Tokens)', 100000, 1000000, true),
                    ('pkg_growth', 'Growth Pack (5M Tokens)', 450000, 5000000, true),
                    ('pkg_scale', 'Enterprise Pack (20M Tokens)', 1600000, 20000000, true)
                ON CONFLICT (""Id"") DO UPDATE 
                SET ""Name"" = EXCLUDED.""Name"", ""FiatPrice"" = EXCLUDED.""FiatPrice"", ""TokenYield"" = EXCLUDED.""TokenYield"";
            ", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 1. Get Wallet status & Topup Packages (App360 pattern)
        group.MapGet("wallet", async (CurrentUser currentUser, IConfiguration config, CancellationToken ct) =>
        {
            var tenantId = currentUser.ExternalUserId ?? "default";
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTablesAsync(conn, ct);

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

        // 2. Get Ledger Transactions History (App360 Parity)
        group.MapGet("wallet/transactions", async (CurrentUser currentUser, IConfiguration config, CancellationToken ct) =>
        {
            var tenantId = currentUser.ExternalUserId ?? "default";
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTablesAsync(conn, ct);

            var list = new List<LedgerTransactionDto>();
            await using (var cmd = new NpgsqlCommand(@"
                SELECT ""Id"", ""TenantId"", ""Amount"", ""BalanceAfter"", ""Type"", ""ReferenceId"", ""Description"", ""CreatedAt""
                FROM user_management.""LedgerTransactions""
                WHERE ""TenantId"" = $1
                ORDER BY ""CreatedAt"" DESC
                LIMIT 100;
            ", conn))
            {
                cmd.Parameters.AddWithValue(tenantId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    list.Add(new LedgerTransactionDto(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetDecimal(2),
                        reader.GetDecimal(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetString(6),
                        reader.GetDateTime(7)
                    ));
                }
            }

            return Results.Ok(list);
        })
        .WithName("GetWalletTransactions")
        .WithSummary("Get user wallet balance history and ledger records (App360 parity)")
        .RequireAuthorization();

        // 3. Initialize PayOS Topup Checkout (with Local/Dev Mock support matching App360)
        group.MapPost("topup", async (
            TopupRequest request,
            HttpContext context,
            CurrentUser currentUser,
            PayOSService payOS,
            IConfiguration config,
            IHostEnvironment env,
            ILogger<BillingEndpoints> logger,
            CancellationToken ct) =>
        {
            var tenantId = currentUser.ExternalUserId ?? "default";
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTablesAsync(conn, ct);

            decimal fiatPrice = 0;
            string pkgName = string.Empty;
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

            if (fiatPrice <= 0)
            {
                return Results.BadRequest(new { message = "Invalid or inactive topup package" });
            }

            var orderCode = long.Parse($"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{Random.Shared.Next(100, 999)}");
            var amount = (int)fiatPrice;

            // Detect whether request is coming from local development environment (App360 pattern)
            var origin = context.Request.Headers.Origin.ToString();
            var referer = context.Request.Headers.Referer.ToString();
            var host = context.Request.Host.Host;

            var isLocal = origin.Contains("localhost") || origin.Contains("127.0.0.1") ||
                          referer.Contains("localhost") || referer.Contains("127.0.0.1") ||
                          host.Contains("localhost") || host.Contains("127.0.0.1") ||
                          env.IsDevelopment() || (request.Mock == true);

            var clientBase = isLocal
                ? (!string.IsNullOrWhiteSpace(origin) ? origin.TrimEnd('/') : !string.IsNullOrWhiteSpace(referer) ? referer.Split('?')[0].TrimEnd('/') : "http://localhost:5173/admin/billings")
                : "https://agent360.antares.vn/admin/billings";

            if (!clientBase.Contains("/admin/billings"))
            {
                clientBase += "/admin/billings";
            }

            var returnUrl = $"{clientBase}?status=TOPUP_PAID&orderCode={orderCode}";
            var cancelUrl = $"{clientBase}?status=CANCELLED&orderCode={orderCode}";

            // LOCAL DEV MODE: Create simulated/mock topup (App360 exact approach)
            if (isLocal)
            {
                logger.LogInformation("[Billing] DEV/LOCAL MODE: Creating mock topup for tenant {TenantId}, pkg {PkgId}, amount {Amount}", tenantId, request.PackageId, amount);

                await using (var txCmd = new NpgsqlCommand(@"
                    INSERT INTO user_management.""PaymentTransactions"" 
                    (""TenantId"", ""OrderCode"", ""Amount"", ""Status"", ""PaymentLinkId"", ""CheckoutUrl"", ""PackageId"", ""CreatedAt"")
                    VALUES ($1, $2, $3, 'pending', $4, $5, $6, NOW());
                ", conn))
                {
                    txCmd.Parameters.AddWithValue(tenantId);
                    txCmd.Parameters.AddWithValue(orderCode);
                    txCmd.Parameters.AddWithValue(fiatPrice);
                    txCmd.Parameters.AddWithValue($"mock_{orderCode}");
                    txCmd.Parameters.AddWithValue(returnUrl);
                    txCmd.Parameters.AddWithValue(request.PackageId);
                    await txCmd.ExecuteNonQueryAsync(ct);
                }

                return Results.Ok(new
                {
                    url = returnUrl,
                    orderCode,
                    amount,
                    isMock = true
                });
            }

            // PRODUCTION MODE: Call real PayOS gateway
            var (checkoutUrl, linkId, err) = await payOS.CreatePaymentLinkAsync(
                orderCode,
                amount,
                $"Topup {tokenYield:#,##0} Tokens".Substring(0, Math.Min(25, $"Topup {tokenYield:#,##0} Tokens".Length)),
                returnUrl,
                cancelUrl,
                ct);

            if (checkoutUrl is null)
            {
                return Results.Problem(err ?? "Failed to create checkout link with PayOS");
            }

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
                amount,
                isMock = false
            });
        })
        .WithName("CreateTopupCheckout")
        .WithSummary("Create PayOS payment link for token wallet topup (supports Local mock)")
        .RequireAuthorization();

        // 4. Payment Sync & Verification Endpoint (Accepts query or body orderCode)
        group.MapPost("sync-order", async (
            HttpContext context,
            IConfiguration config,
            ILogger<BillingEndpoints> logger,
            CancellationToken ct) =>
        {
            long orderCode = 0;
            if (context.Request.Query.TryGetValue("orderCode", out var qCode) && long.TryParse(qCode, out var parsedQ))
            {
                orderCode = parsedQ;
            }
            else
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("orderCode", out var bCode))
                    {
                        orderCode = bCode.GetInt64();
                    }
                }
                catch {}
            }

            if (orderCode <= 0)
            {
                return Results.BadRequest(new { message = "orderCode is required" });
            }

            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            await EnsureTablesAsync(conn, ct);

            string? tenantId = null;
            string? pkgId = null;
            string status = "pending";
            decimal tokenYield = 0;

            await using (var cmd = new NpgsqlCommand(@"
                SELECT t.""TenantId"", t.""PackageId"", t.""Status"", COALESCE(p.""TokenYield"", 0)
                FROM user_management.""PaymentTransactions"" t
                LEFT JOIN user_management.""TopupPackages"" p ON t.""PackageId"" = p.""Id""
                WHERE t.""OrderCode"" = $1;
            ", conn))
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

            if (tenantId is null) return Results.NotFound(new { message = "Transaction not found" });

            if (status != "paid" && status != "success")
            {
                // Mark paid, credit tokens and insert ledger transaction record (App360 parity)
                await using var tx = await conn.BeginTransactionAsync(ct);
                try
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

                    await using (var creditCmd = new NpgsqlCommand(@"
                        UPDATE user_management.""PaymentTransactions""
                        SET ""Status"" = 'paid', ""CompletedAt"" = NOW()
                        WHERE ""OrderCode"" = $1;

                        UPDATE user_management.""Wallets""
                        SET ""BalanceTokens"" = $2,
                            ""UpdatedAt"" = NOW()
                        WHERE ""TenantId"" = $3;

                        INSERT INTO user_management.""LedgerTransactions""
                        (""TenantId"", ""Amount"", ""BalanceAfter"", ""Type"", ""ReferenceId"", ""Description"", ""CreatedAt"")
                        VALUES ($3, $4, $2, 'TOPUP', $5, $6, NOW());
                    ", conn, tx))
                    {
                        creditCmd.Parameters.AddWithValue(orderCode);
                        creditCmd.Parameters.AddWithValue(newBalance);
                        creditCmd.Parameters.AddWithValue(tenantId);
                        creditCmd.Parameters.AddWithValue(tokenYield);
                        creditCmd.Parameters.AddWithValue($"order_{orderCode}");
                        creditCmd.Parameters.AddWithValue($"PayOS Token Topup (+{tokenYield:#,##0} tokens)");
                        await creditCmd.ExecuteNonQueryAsync(ct);
                    }

                    await tx.CommitAsync(ct);
                    logger.LogInformation("[Billing Sync] Order {OrderCode} verified. Credited {Tokens} tokens to {TenantId}. New balance: {NewBalance}", 
                        orderCode, tokenYield, tenantId, newBalance);

                    await AuditLogger.LogAsync(
                        config, null, tenantId, "TOPUP_CONFIRMED", $"order:{orderCode}",
                        new { order_code = orderCode, tokens = tokenYield, package_id = pkgId, balance_after = newBalance }, ct: ct);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogError(ex, "[Billing Sync] Failed to credit tokens for order {OrderCode}", orderCode);
                    return Results.Problem(ex.Message);
                }
            }

            return Results.Ok(new { success = true, orderCode, status = "paid", tokensCredited = tokenYield });
        })
        .WithName("SyncPaymentOrder")
        .WithSummary("Confirm and credit PayOS token order with ledger history")
        .AllowAnonymous();
    }
}
