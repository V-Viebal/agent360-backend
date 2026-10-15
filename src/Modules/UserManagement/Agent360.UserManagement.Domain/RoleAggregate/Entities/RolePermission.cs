using Hmz.Core.SharedKernel.Data;
using Agent360.UserManagement.Domain.PermissionAggregate;

namespace Agent360.UserManagement.Domain.RoleAggregate.Entities;

public sealed class RolePermission : AuditableEntityBase<RolePermission>
{
    public long RoleId { get; private set; }
    public long PermissionId { get; private set; }
    public Role Role { get; private set; } = default!;
    public Permission Permission { get; private set; } = default!;

    private RolePermission() { }
    public RolePermission(long roleId, long permissionId) => (RoleId, PermissionId) = (roleId, permissionId);
}