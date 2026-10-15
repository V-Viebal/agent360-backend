using Hmz.Core.SharedKernel.Data.Configurations;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.UserAggregate.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agent360.UserManagement.Infrastructure.Persistence.Configurations;

internal class UserRoleConfiguration : EntityTypeConfigurationBuilderBase<UserRole, long>
{
    public override void ConfigureEntitySpecific(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles");

        builder.HasKey(x => new { x.UserId, x.RoleId });

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}