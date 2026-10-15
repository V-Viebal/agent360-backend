using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.UserAggregate;

namespace Agent360.UserManagement.UseCase.Users.Common;

public sealed record UserResponse(
    long Id,
    string ExternalUserId,
    string? Email,
    string FirstName,
    string LastName,
    string? DisplayName,
    string? AvatarUrl,
    string Status,
    uint RowVersion,
    IReadOnlyList<UserRoleResponse> Roles)
{
    public static UserResponse From(
        User user,
        IReadOnlyDictionary<long, Role> rolesById) =>
        new(
            user.Id,
            user.LogtoUserId,
            user.Email?.Value,
            user.FirstName,
            user.LastName,
            user.DisplayName,
            user.AvatarUrl,
            user.Status.Name,
            user.RowVersion,
            user.Roles
                .Select(userRole => rolesById.GetValueOrDefault(userRole.RoleId))
                .Where(role => role is not null)
                .Select(role => new UserRoleResponse(
                    role!.Id,
                    role.Code,
                    role.Name,
                    role.IsSystemRole))
                .OrderBy(role => role.Code, StringComparer.Ordinal)
                .ToArray());
}

public sealed record UserRoleResponse(
    long Id,
    string Code,
    string Name,
    bool IsSystemRole);