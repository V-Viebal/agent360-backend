using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.Domain.RoleAggregate;
using Microsoft.EntityFrameworkCore;

namespace Agent360.UserManagement.Infrastructure.Persistence.Seeds;

public static class RoleSeeder
{
    public static async Task SeedAsync(AppDbContext context, CancellationToken ct = default)
    {
        var predefinedRoles = new List<(string Code, string Name, bool IsSystemRole)>
        {
            (RoleCodes.Customer, nameof(RoleCodes.Customer), false),
            (RoleCodes.Admin, nameof(RoleCodes.Admin), false),
            (RoleCodes.System, nameof(RoleCodes.System), true),
            (RoleCodes.External, nameof(RoleCodes.External), false),
        };

        foreach (var (code, name, isSystemRole) in predefinedRoles)
        {
            var existing = await context.Roles.FirstOrDefaultAsync(x => x.Code == code, ct);

            if (existing is null)
            {
                context.Roles.Add(Role.Create(name, code, isSystemRole));
            }
            else if (!existing.IsSystemRole)
            {
                existing.Rename(name);
            }
        }

        await context.SaveChangesAsync(ct);
    }
}