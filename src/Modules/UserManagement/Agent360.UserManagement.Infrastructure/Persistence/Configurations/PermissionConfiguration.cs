using Hmz.Core.SharedKernel.Data.Configurations;
using Agent360.UserManagement.Domain.PermissionAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agent360.UserManagement.Infrastructure.Persistence.Configurations;

internal sealed class PermissionConfiguration : EntityTypeConfigurationBuilderBase<Permission, long>
{
    public override void ConfigureEntitySpecific(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).IsRequired().HasMaxLength(Permission.MaxCodeLength);

        builder.HasIndex(x => x.Code)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.Property(x => x.Name).IsRequired().HasMaxLength(Permission.MaxNameLength);

        builder.Property(x => x.Description).HasMaxLength(500);

        builder.Navigation(x => x.RolePermissions)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .Metadata.SetField("_rolePermissions");
    }
}