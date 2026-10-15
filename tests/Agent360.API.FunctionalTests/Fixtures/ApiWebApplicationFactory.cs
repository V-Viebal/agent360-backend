using Hmz.Core.Testing.Authentication;
using Hmz.Core.Testing.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReferenceDataDbContext =
    Agent360.ReferenceData.Infrastructure.Persistence.AppDbContext;
using UserManagementDbContext =
    Agent360.UserManagement.Infrastructure.Persistence.AppDbContext;

namespace Agent360.API.FunctionalTests.Fixtures;

public sealed class ApiWebApplicationFactory(
    string connectionString,
    string? environment = null)
    : ApiWebApplicationFactory<Program>
{
    public const string WebhookSigningKey = "functional-test-webhook-key";

    protected override string TestEnvironment =>
        environment ?? base.TestEnvironment;

    protected override void ConfigureApplicationWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Testing:IsTestHost", bool.TrueString);
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("Logto:Webhooks:SignUpSigningKey", WebhookSigningKey);

        builder.ConfigureTestServices(services =>
        {
            services.AddHmzTestAuthentication();

            ReplaceDbContext<ReferenceDataDbContext>(services, "reference_data");
            ReplaceDbContext<UserManagementDbContext>(services, "user_management");
        });
    }

    private void ReplaceDbContext<TContext>(
        IServiceCollection services,
        string migrationHistorySchema)
        where TContext : DbContext
    {
        services.RemoveAll<DbContextOptions<TContext>>();
        services.RemoveAll<TContext>();
        services.AddDbContext<TContext>(options =>
        {
            options.UseNpgsql(
                connectionString,
                npgsql =>
                {
                    npgsql.MigrationsAssembly(typeof(TContext).Assembly.FullName);
                    npgsql.MigrationsHistoryTable(
                        "__EFMigrationsHistory",
                        migrationHistorySchema);
                });
        });
    }
}