using Agent360.UserManagement.Infrastructure.Persistence.Seeds;
using Microsoft.Extensions.DependencyInjection;

namespace Agent360.UserManagement.Infrastructure.Persistence;

public static class DataSeeder
{
    /// <summary>
    /// Seeds roles, permissions, and system users after a separate migration
    /// operation has prepared this module's schema. This method never migrates.
    /// </summary>
    public static async Task SeedDataAsync(
        IServiceProvider sp,
        CancellationToken cancellationToken = default)
    {
        await using var scope = sp.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await RoleSeeder.SeedAsync(context, cancellationToken);
        await PermissionSeeder.SeedAsync(context, cancellationToken);
        await UserSeeder.SeedAsync(context, cancellationToken);
    }
}