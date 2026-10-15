using Hmz.Core.SharedKernel.Data.Configurations;
using Agent360.ReferenceData.Domain.LanguageAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agent360.ReferenceData.Infrastructure.Persistence.Configurations;

internal class LanguageConfiguration : EntityTypeConfigurationBuilderBase<Language, long>
{
    public override void ConfigureEntitySpecific(EntityTypeBuilder<Language> builder)
    {
        builder.ToTable("Languages");

        builder.Property(x => x.Code)
            .IsRequired()
            .HasMaxLength(10);

        builder.HasIndex(x => x.Code)
            .IsUnique();

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.HasIndex(x => x.IsActive);
    }
}