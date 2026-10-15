using Agent360.UserManagement.Domain.RoleAggregate;

namespace Agent360.UserManagement.UseCase.Roles.Common;

public sealed record RoleResponse(
    long Id,
    string Name,
    string Code,
    bool IsSystemRole,
    uint RowVersion)
{
    public static RoleResponse From(Role role) => new(
        role.Id,
        role.Name,
        role.Code,
        role.IsSystemRole,
        role.RowVersion);
}