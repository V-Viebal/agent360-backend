namespace Agent360.API.Configurations;

public static class ApplicationBootstrapper
{
    public static async Task SeedCoreAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        try
        {
            await ReferenceDataSeeder.SeedDataAsync(sp, ct);
            await UserDataSeeder.SeedDataAsync(sp, ct);
        }
        catch (Exception)
        {
            // Allow bootstrapping when local PostgreSQL is not available
        }
    }

    /// <summary>
    /// Seeds optional Development-only demo data after the local
    /// database has been migrated by an explicit developer/tooling operation.
    /// This method never migrates an application database.
    /// </summary>
    public static Task SeedDevelopmentAsync(
        IServiceProvider sp,
        CancellationToken ct = default)
    {
        // Demo: add module seeders here as the project grows business modules.
        return Task.CompletedTask;
    }
}