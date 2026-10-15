using Agent360.ReferenceData.Domain.CurrencyAggregate;
using Microsoft.EntityFrameworkCore;

namespace Agent360.ReferenceData.Infrastructure.Persistence.Seeds;

public static class CurrencySeeder
{
    public static async Task SeedAsync(AppDbContext context, CancellationToken ct = default)
    {
        var currencies = new[]
        {
            ("VND", "đ", 0),
            ("USD", "$", 2),
        };

        foreach (var (code, symbol, digits) in currencies)
        {
            var existing = await context.Currencies.FirstOrDefaultAsync(c => c.Code == code, ct);

            if (existing is null)
            {
                context.Currencies.Add(Currency.Create(code, symbol, digits));
            }
        }

        await context.SaveChangesAsync(ct);
    }
}