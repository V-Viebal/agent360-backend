using Hmz.Core.SharedKernel.Data.Configurations;
using Agent360.UserManagement.Domain.RoleAggregate.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agent360.UserManagement.Infrastructure.Persistence.Configurations;

internal sealed class RolePermissionConfiguration : EntityTypeConfigurationBuilderBase<RolePermission, long>
{
    public override void ConfigureEntitySpecific(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");

        builder.HasKey(x => new { x.RoleId, x.PermissionId });

        builder.HasOne(x => x.Role).WithMany(x => x.Permissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Permission).WithMany(x => x.RolePermissions).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PermissionId);
    }
}