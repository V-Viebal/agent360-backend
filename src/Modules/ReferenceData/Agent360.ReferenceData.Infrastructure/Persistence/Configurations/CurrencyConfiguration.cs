using Hmz.Core.SharedKernel.Data.Configurations;
using Agent360.ReferenceData.Domain.CurrencyAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agent360.ReferenceData.Infrastructure.Persistence.Configurations;

internal class CurrencyConfiguration : EntityTypeConfigurationBuilderBase<Currency, long>
{
    public override void ConfigureEntitySpecific(EntityTypeBuilder<Currency> builder)
    {
        builder.ToTable("Currencies");

        builder.HasIndex(x => x.Code)
            .IsUnique();

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(x => x.DecimalDigits)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .IsRequired();

        // Optional: filter index for active currencies
        builder.HasIndex(x => x.IsActive);
    }
}