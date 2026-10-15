using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.UserAggregate;
using Microsoft.EntityFrameworkCore;

namespace Agent360.UserManagement.Infrastructure.Persistence.Seeds;

public static class UserSeeder
{
    public static async Task SeedAsync(AppDbContext context, CancellationToken cancellationToken = default)
    {
        var users = new List<(string RoleCode, string ExternalUserId, string Email, string FirstName, string LastName)>
        {
            (RoleCodes.System, SystemUsers.SystemId, SystemUsers.SystemEmail, "System", "User"),
            (RoleCodes.System, SystemUsers.SeederId, SystemUsers.SeederEmail, "Seeder", "User"),
            (RoleCodes.System, SystemUsers.BackgroundJobId, SystemUsers.BackgroundJobEmail, "Background Job", "User"),
            (RoleCodes.External, SystemUsers.ExternalId, SystemUsers.ExternalEmail, "Logto", "User"),
        };

        var roles = await context.Roles
            .IgnoreQueryFilters()
            .ToDictionaryAsync(x => x.Code, cancellationToken);

        foreach (var user in users)
        {
            await SeedSystemUserAsync(
                context,
                roles,
                user.ExternalUserId,
                user.Email,
                user.FirstName,
                user.LastName,
                user.RoleCode,
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedSystemUserAsync(AppDbContext context, Dictionary<string, Role> roles, string externalUserId, string email, string firstName, string lastName, string roleCode, CancellationToken cancellationToken)
    {
        var existing = await context.Users
                .FirstOrDefaultAsync(u => u.LogtoUserId == externalUserId || u.Email!.Value == email, cancellationToken);

        if (existing is null)
        {
            if (!roles.TryGetValue(roleCode, out var role))
            {
                throw new InvalidOperationException(
                    $"Role '{roleCode}' was not seeded.");
            }

            var user = User.CreateSystemUser(externalUserId, email, firstName, lastName);

            user.AssignRole(role.Id);

            context.Users.Add(user);
        }
    }
}