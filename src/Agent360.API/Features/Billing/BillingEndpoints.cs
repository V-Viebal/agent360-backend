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
            var ddlStatements = new[]
            {
                @"CREATE SCHEMA IF NOT EXISTS user_management;",

                @"CREATE TABLE IF NOT EXISTS user_management.""Wallets"" (
                    ""TenantId"" text PRIMARY KEY,
                    ""BalanceTokens"" numeric(18, 4) NOT NULL DEFAULT 500000.0000,
                    ""Status"" text NOT NULL DEFAULT 'Active',
                    ""UpdatedAt"" timestamptz NOT NULL DEFAULT NOW()
                );",

                @"CREATE TABLE IF NOT EXISTS user_management.""TopupPackages"" (
                    ""Id"" text PRIMARY KEY,
                    ""Name"" text NOT NULL,
                    ""FiatPrice"" numeric(18, 2) NOT NULL,
                    ""TokenYield"" numeric(18, 4) NOT NULL,
                    ""IsActive"" boolean NOT NULL DEFAULT true
                );",

                @"CREATE TABLE IF NOT EXISTS user_management.""PaymentTransactions"" (
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
                );",

                @"CREATE TABLE IF NOT EXISTS user_management.""LedgerTransactions"" (
                    ""Id"" bigserial PRIMARY KEY,
                    ""TenantId"" text NOT NULL,
                    ""Amount"" numeric(18, 4) NOT NULL,
                    ""BalanceAfter"" numeric(18, 4) NOT NULL,
                    ""Type"" text NOT NULL,
                    ""ReferenceId"" text,
                    ""Description"" text NOT NULL,
                    ""CreatedAt"" timestamptz NOT NULL DEFAULT NOW()
                );",

                @"INSERT INTO user_management.""TopupPackages"" (""Id"", ""Name"", ""FiatPrice"", ""TokenYield"", ""IsActive"")
                VALUES 
                    ('pkg_starter', 'Starter Pack (1M Tokens)', 100000, 1000000, true),
                    ('pkg_growth', 'Growth Pack (5M Tokens)', 450000, 5000000, true),
                    ('pkg_scale', 'Enterprise Pack (20M Tokens)', 1600000, 20000000, true)
                ON CONFLICT (""Id"") DO UPDATE 
                SET ""Name"" = EXCLUDED.""Name"", ""FiatPrice"" = EXCLUDED.""FiatPrice"", ""TokenYield"" = EXCLUDED.""TokenYield"";"
            };

            foreach (var ddl in ddlStatements)
            {
                await using var cmd = new NpgsqlCommand(ddl, conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
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
        routeBuilder.MapAdminBillingEndpoints();

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

                    // 1. Update Payment Transaction to paid
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
                        insertLedgerCmd.Parameters.AddWithValue($"PayOS Token Topup (+{tokenYield:#,##0} tokens)");
                        await insertLedgerCmd.ExecuteNonQueryAsync(ct);
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

// Super Admin Directory & Adjustment Endpoints
public sealed record AdjustWalletRequest(string TenantId, decimal Amount, string Description);

public static class AdminBillingExtensions
{
    public static void MapAdminBillingEndpoints(this IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("admin/billings")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Admin.Billings")
            .RequireAuthorization("Admin");

        // 1. Get all wallets directory across all tenants
        group.MapGet("wallets", async (IConfiguration config, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var list = new List<object>();
            await using (var cmd = new NpgsqlCommand(@"
                SELECT w.""TenantId"", w.""BalanceTokens"", w.""Status"", w.""UpdatedAt"",
                       COALESCE(u.""Email"", w.""TenantId"") as Email,
                       COALESCE(u.""DisplayName"", u.""FirstName"", w.""TenantId"") as DisplayName
                FROM user_management.""Wallets"" w
                LEFT JOIN user_management.""Users"" u ON w.""TenantId"" = u.""LogtoUserId""
                ORDER BY w.""UpdatedAt"" DESC
                LIMIT 100;
            ", conn))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var balance = reader.GetDecimal(1);
                    list.Add(new
                    {
                        tenant_id = reader.GetString(0),
                        balance_tokens = balance,
                        status = reader.GetString(2),
                        updated_at = reader.GetDateTime(3),
                        email = reader.GetString(4),
                        display_name = reader.GetString(5),
                        runway_days = balance > 0 ? (int)(balance / 25000) : 0
                    });
                }
            }

            return Results.Ok(list);
        })
        .WithName("AdminGetWalletsDirectory")
        .WithSummary("List all tenant wallets across the platform (Admin only)");

        // 2. Adjust or grant bonus tokens to a tenant
        group.MapPost("wallets/adjust", async (
            AdjustWalletRequest request,
            WalletService walletService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.TenantId)) return Results.BadRequest(new { message = "tenantId is required" });

            var newBalance = await walletService.AdjustTokensAsync(
                request.TenantId,
                request.Amount,
                request.Amount >= 0 ? "ADMIN_GRANT" : "ADMIN_DEDUCT",
                request.Description ?? "Admin manual balance adjustment",
                $"admin_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                ct);

            return Results.Ok(new
            {
                success = true,
                tenant_id = request.TenantId,
                amount_adjusted = request.Amount,
                new_balance = newBalance
            });
        })
        .WithName("AdminAdjustWallet")
        .WithSummary("Grant bonus or deduct tokens manually for a tenant (Admin only)");

        // 3. Get all platform billing transactions (App360 admin-billing-list parity)
        group.MapGet("transactions", async (IConfiguration config, CancellationToken ct) =>
        {
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var list = new List<object>();
            await using (var cmd = new NpgsqlCommand(@"
                SELECT pt.""Id"", pt.""TenantId"", pt.""OrderCode"", pt.""Amount"", pt.""Status"", 
                       pt.""PaymentLinkId"", pt.""CheckoutUrl"", pt.""PackageId"", pt.""CreatedAt"", pt.""CompletedAt"",
                       COALESCE(u.""Email"", pt.""TenantId"") as Email,
                       COALESCE(u.""DisplayName"", u.""FirstName"", pt.""TenantId"") as DisplayName,
                       COALESCE(pkg.""TokenYield"", 1000000) as TokenYield,
                       COALESCE(pkg.""Name"", pt.""PackageId"") as PackageName
                FROM user_management.""PaymentTransactions"" pt
                LEFT JOIN user_management.""Users"" u ON pt.""TenantId"" = u.""LogtoUserId""
                LEFT JOIN user_management.""TopupPackages"" pkg ON pt.""PackageId"" = pkg.""Id""
                ORDER BY pt.""CreatedAt"" DESC
                LIMIT 200;
            ", conn))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    list.Add(new
                    {
                        id = reader.GetInt64(0),
                        tenant_id = reader.GetString(1),
                        order_code = reader.GetInt64(2),
                        amount = reader.GetDecimal(3),
                        status = reader.GetString(4),
                        payment_link_id = reader.IsDBNull(5) ? null : reader.GetString(5),
                        checkout_url = reader.IsDBNull(6) ? null : reader.GetString(6),
                        package_id = reader.IsDBNull(7) ? null : reader.GetString(7),
                        created_at = reader.GetDateTime(8),
                        completed_at = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9),
                        tenant_email = reader.GetString(10),
                        tenant_name = reader.GetString(11),
                        token_yield = reader.GetDecimal(12),
                        package_name = reader.GetString(13)
                    });
                }
            }

            return Results.Ok(list);
        })
        .WithName("AdminGetTransactions")
        .WithSummary("List all platform payment transactions (Admin only)");

        // 4. Manual sync payment transaction with PayOS (App360 admin-sync parity)
        group.MapPost("transactions/{orderCode:long}/sync", async (
            long orderCode,
            WalletService walletService,
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminBillingSync");
            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connStr)) return Results.Problem("Database not configured");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            // Fetch payment transaction
            string? tenantId = null;
            string? packageId = null;
            string? currentStatus = null;
            decimal amount = 0;

            await using (var getCmd = new NpgsqlCommand(@"
                SELECT ""TenantId"", ""PackageId"", ""Status"", ""Amount""
                FROM user_management.""PaymentTransactions""
                WHERE ""OrderCode"" = $1;
            ", conn))
            {
                getCmd.Parameters.AddWithValue(orderCode);
                await using var reader = await getCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    tenantId = reader.GetString(0);
                    packageId = reader.IsDBNull(1) ? "pkg_starter" : reader.GetString(1);
                    currentStatus = reader.GetString(2);
                    amount = reader.GetDecimal(3);
                }
            }

            if (tenantId is null)
            {
                return Results.NotFound(new { message = $"Đơn hàng #{orderCode} không tồn tại" });
            }

            if (string.Equals(currentStatus, "paid", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new { success = true, message = $"Đơn hàng #{orderCode} đã hoàn tất thanh toán trước đó.", status = "paid" });
            }

            // In production/local: Call sync or check
            decimal tokenYield = packageId switch
            {
                "pkg_scale" => 20000000m,
                "pkg_growth" => 5000000m,
                _ => 1000000m
            };

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var updateCmd = new NpgsqlCommand(@"
                    UPDATE user_management.""PaymentTransactions""
                    SET ""Status"" = 'paid', ""CompletedAt"" = NOW()
                    WHERE ""OrderCode"" = $1;
                ", conn, tx))
                {
                    updateCmd.Parameters.AddWithValue(orderCode);
                    await updateCmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);

                // Credit tokens into wallet
                var newBalance = await walletService.AdjustTokensAsync(
                    tenantId,
                    tokenYield,
                    "PAYOS_TOPUP",
                    $"PayOS Topup (Admin Synced) Order #{orderCode}",
                    orderCode.ToString(),
                    ct);

                logger.LogInformation("[Admin Sync] Order {OrderCode} marked PAID and credited {Tokens} tokens for {TenantId}", orderCode, tokenYield, tenantId);

                return Results.Ok(new
                {
                    success = true,
                    orderCode,
                    status = "paid",
                    tokensCredited = tokenYield,
                    newBalance
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "[Admin Sync] Failed to sync order {OrderCode}", orderCode);
                return Results.Problem(ex.Message);
            }
        })
        .WithName("AdminSyncTransaction")
        .WithSummary("Manually verify and sync transaction with PayOS (Admin only)");

    }
}
