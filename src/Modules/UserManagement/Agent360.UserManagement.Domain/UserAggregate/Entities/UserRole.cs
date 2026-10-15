using Hmz.Core.SharedKernel.Data;

namespace Agent360.UserManagement.Domain.UserAggregate.Entities;

public sealed class UserRole : AuditableEntityBase<UserRole>
{
    public long UserId { get; private set; }
    public long RoleId { get; private set; }

    private UserRole() { }

    public UserRole(long userId, long roleId)
    {
        UserId = userId;
        RoleId = roleId;
    }
}