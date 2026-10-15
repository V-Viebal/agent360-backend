using Hmz.Core.SharedKernel.Data.Configurations;
using Agent360.UserManagement.Domain.RoleAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agent360.UserManagement.Infrastructure.Persistence.Configurations;

internal class RoleConfiguration : EntityTypeConfigurationBuilderBase<Role, long>
{
    public override void ConfigureEntitySpecific(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");

        builder.Property(x => x.Code)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(x => x.Code)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.Property(x => x.PermissionStamp).IsRequired();

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Navigation(x => x.Permissions)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .Metadata.SetField("_permissions");
    }
}