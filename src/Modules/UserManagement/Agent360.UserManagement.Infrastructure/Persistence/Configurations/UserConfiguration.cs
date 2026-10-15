using Hmz.Core.SharedKernel.Data.Configurations;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.Domain.UserAggregate.Entities;
using Agent360.UserManagement.Domain.UserAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agent360.UserManagement.Infrastructure.Persistence.Configurations;

internal class UserConfiguration : EntityTypeConfigurationBuilderBase<User, long>
{
    public override void ConfigureEntitySpecific(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.Property(x => x.PermissionStamp).IsRequired();

        builder.Property(x => x.LogtoUserId)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(x => x.LogtoUserId)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.Property(x => x.FirstName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.LastName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.DisplayName)
            .HasMaxLength(200);

        builder.Property(x => x.AvatarUrl)
            .HasMaxLength(500);

        builder.Property(x => x.PreferredLanguageCode)
            .HasMaxLength(10);

        builder.ComplexProperty(x => x.Email, email =>
        {
            email.Property(e => e.Value)
                .HasColumnName("Email")
                .IsRequired()
                .HasMaxLength(256);
        });

        builder.ComplexProperty(x => x.PhoneNumber, phoneNumber =>
        {
            phoneNumber.Property(p => p.CountryCode).HasColumnName("PhoneCountryCode");
            phoneNumber.Property(p => p.Number).HasColumnName("PhoneNumber");
            phoneNumber.Property(p => p.Extension).HasColumnName("PhoneExtension");
        });

        builder.Property(x => x.Status)
            .HasConversion(
                x => x.Value,
                x => UserStatus.FromValue(x));

        // Backing field
        builder.Navigation(x => x.Roles)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .Metadata.SetField("_roles");

        builder.HasMany(x => x.Roles)
            .WithOne()
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}