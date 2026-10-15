using Hmz.Core.Testing.PostgreSql;
using Agent360.API.Configurations;
using Agent360.API.FunctionalTests.Services.ApiClients;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Respawn.Graph;
using ReferenceDataDbContext = Agent360.ReferenceData.Infrastructure.Persistence.AppDbContext;
using UserManagementDbContext = Agent360.UserManagement.Infrastructure.Persistence.AppDbContext;

namespace Agent360.API.FunctionalTests.Fixtures;

public sealed class FunctionalTestFixture
    : PostgreSqlFunctionalTestFixtureBase<
        ApiWebApplicationFactory,
        FunctionalApiClient>
{
    protected override IReadOnlyCollection<string> RespawnSchemas =>
        ["user_management", "reference_data"];

    protected override IReadOnlyCollection<Table> RespawnTablesToIgnore =>
        [
            new("user_management", "__EFMigrationsHistory"),
            new("reference_data", "__EFMigrationsHistory")
        ];

    public ApiWebApplicationFactory CreateApplicationFactory(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        return new ApiWebApplicationFactory(ConnectionString, environment);
    }

    protected override ApiWebApplicationFactory CreateFactory(
        string connectionString) =>
        new(connectionString);

    protected override FunctionalApiClient CreateApiClient(HttpClient client) =>
        new(client);

    protected override async Task InitializeApplicationServicesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // The TestEnvironment host does not seed at startup. The fixture owns
        // schema creation and core seed timing; Development test hosts still
        // run core seeding and suppress only optional demo data.
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReferenceDataDbContext>()
            .Database.MigrateAsync(cancellationToken);

        await scope.ServiceProvider.GetRequiredService<UserManagementDbContext>()
            .Database.MigrateAsync(cancellationToken);
    }

    protected override Task SeedApplicationAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ApplicationBootstrapper.SeedCoreAsync(services);

    protected override async Task SeedTestDataAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<UserManagementDbContext>();

        var adminRoleId = await context.Roles
            .Where(role => role.Code == RoleCodes.Admin)
            .Select(role => role.Id)
            .SingleAsync(cancellationToken);

        var admin = User.CreateSystemUser(
            TestActors.AdminSubject,
            TestActors.AdminEmail,
            "Functional",
            "Administrator");

        admin.AssignRole(adminRoleId);

        var customerRoleId = await context.Roles
            .Where(role => role.Code == RoleCodes.Customer)
            .Select(role => role.Id)
            .SingleAsync(cancellationToken);

        var customer = User.CreateSystemUser(
            TestActors.CustomerSubject,
            TestActors.CustomerEmail,
            "Functional",
            "Customer");

        customer.AssignRole(customerRoleId);

        context.Users.AddRange(admin, customer);
        await context.SaveChangesAsync(cancellationToken);

        await scope.ServiceProvider.GetRequiredService<IUserIdentityService>()
            .InvalidateAsync(TestActors.AdminSubject, cancellationToken);

        await scope.ServiceProvider.GetRequiredService<IPermissionCacheInvalidator>()
            .InvalidateUserAsync(admin.Id, cancellationToken);

        await scope.ServiceProvider.GetRequiredService<IUserIdentityService>()
            .InvalidateAsync(TestActors.CustomerSubject, cancellationToken);

        await scope.ServiceProvider.GetRequiredService<IPermissionCacheInvalidator>()
            .InvalidateUserAsync(customer.Id, cancellationToken);
    }
}