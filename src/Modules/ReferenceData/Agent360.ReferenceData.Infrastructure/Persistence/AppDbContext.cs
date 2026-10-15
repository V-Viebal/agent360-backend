using Agent360.ReferenceData.Domain.CurrencyAggregate;
using Agent360.ReferenceData.Domain.LanguageAggregate;
using Microsoft.EntityFrameworkCore;

namespace Agent360.ReferenceData.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> dbContextOptions) : DbContext(dbContextOptions)
{
    public DbSet<Language> Languages => this.Set<Language>();
    public DbSet<Currency> Currencies => this.Set<Currency>();

    override protected void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("reference_data");
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
    }
}