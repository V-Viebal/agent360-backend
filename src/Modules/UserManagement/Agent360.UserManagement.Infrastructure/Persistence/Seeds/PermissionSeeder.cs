using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.Domain.PermissionAggregate;
using Microsoft.EntityFrameworkCore;

namespace Agent360.UserManagement.Infrastructure.Persistence.Seeds;

public static class PermissionSeeder
{
    public static async Task SeedAsync(AppDbContext context, CancellationToken ct = default)
    {
        foreach (var item in UserManagementPermissions.Catalog)
        {
            if (!await context.Permissions.AnyAsync(x => x.Code == item.Code, ct))
                context.Permissions.Add(Permission.Create(item.Code, item.Name, item.Description));
        }
        await context.SaveChangesAsync(ct);

        var permissionIds = await context.Permissions
            .ToDictionaryAsync(x => x.Code, x => x.Id, ct);

        var admin = await context.Roles
            .Include(x => x.Permissions)
            .AsTracking()
            .SingleAsync(x => x.Code == RoleCodes.Admin, ct);
        admin.SeedPermissions(permissionIds.Values);

        // Internal SYSTEM users keep broad read/management access but cannot
        // assign user roles. That administrative capability is ADMIN-only.
        var system = await context.Roles
            .Include(x => x.Permissions)
            .AsTracking()
            .SingleAsync(x => x.Code == RoleCodes.System, ct);
        system.SeedPermissions(permissionIds
            .Where(item => item.Key != UserManagementPermissions.Users.ManageRoles)
            .Select(item => item.Value));

        await context.SaveChangesAsync(ct);
    }
}