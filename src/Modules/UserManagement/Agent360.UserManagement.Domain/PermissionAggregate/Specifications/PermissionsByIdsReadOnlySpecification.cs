using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.PermissionAggregate.Specifications;

public sealed class PermissionsByIdsReadOnlySpecification : Specification<Permission>
{
    public PermissionsByIdsReadOnlySpecification(IEnumerable<long> permissionIds)
    {
        var ids = permissionIds.Distinct().ToArray();
        Query.AsNoTracking().Where(permission => ids.Contains(permission.Id));
    }
}