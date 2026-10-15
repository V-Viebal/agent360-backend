using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.RoleAggregate.Specifications;

public sealed class RolesByIdsReadOnlySpecification : Specification<Role>
{
    public RolesByIdsReadOnlySpecification(IEnumerable<long> roleIds)
    {
        var ids = roleIds.Distinct().ToArray();

        Query
            .AsNoTracking()
            .Where(role => ids.Contains(role.Id))
            .OrderBy(role => role.Code);
    }
}