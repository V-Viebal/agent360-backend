using Agent360.ReferenceData.Domain.LanguageAggregate;
using Microsoft.EntityFrameworkCore;

namespace Agent360.ReferenceData.Infrastructure.Persistence.Seeds;

public static class LanguageSeeder
{
    public static async Task SeedAsync(AppDbContext context, CancellationToken ct = default)
    {
        var languages = new[]
        {
            ("en", "English"),
            ("vi", "Vietnamese"),
        };

        foreach (var (code, name) in languages)
        {
            var existing = await context.Languages.FirstOrDefaultAsync(l => l.Code == code, ct);
            if (existing is null)
            {
                context.Languages.Add(Language.Create(name, code));
            }
        }

        await context.SaveChangesAsync(ct);
    }
}