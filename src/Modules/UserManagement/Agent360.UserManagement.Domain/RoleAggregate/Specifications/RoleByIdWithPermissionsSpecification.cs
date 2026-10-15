using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.RoleAggregate.Specifications;

public sealed class RoleByIdWithPermissionsSpecification : SingleResultSpecification<Role>
{
    public RoleByIdWithPermissionsSpecification(long roleId)
    {
        Query
            .AsTracking()
            .Where(role => role.Id == roleId);

        Query.Include(role => role.Permissions).ThenInclude(x => x.Permission);
    }
}