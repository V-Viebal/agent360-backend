using Agent360.UserManagement.Domain.PermissionAggregate;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.UserAggregate;
using Microsoft.EntityFrameworkCore;

namespace Agent360.UserManagement.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> dbContextOptions) : DbContext(dbContextOptions)
{
    public DbSet<User> Users => this.Set<User>();
    public DbSet<Role> Roles => this.Set<Role>();
    public DbSet<Permission> Permissions => this.Set<Permission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("user_management");
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
    }
}