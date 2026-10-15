using Agent360.ReferenceData.Infrastructure.Persistence.Seeds;
using Microsoft.Extensions.DependencyInjection;

namespace Agent360.ReferenceData.Infrastructure.Persistence;

public static class DataSeeder
{
    /// <summary>
    /// Seeds reference data after a separate migration operation has prepared
    /// this module's schema. This method never applies migrations.
    /// </summary>
    public static async Task SeedDataAsync(
        IServiceProvider sp,
        CancellationToken cancellationToken = default)
    {
        await using var scope = sp.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await LanguageSeeder.SeedAsync(context, cancellationToken);
        await CurrencySeeder.SeedAsync(context, cancellationToken);
    }
}