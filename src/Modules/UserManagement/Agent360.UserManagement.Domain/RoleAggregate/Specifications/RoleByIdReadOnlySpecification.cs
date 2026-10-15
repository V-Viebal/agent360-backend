using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.RoleAggregate.Specifications;

public sealed class RoleByIdReadOnlySpecification : Specification<Role>
{
    public RoleByIdReadOnlySpecification(long roleId)
    {
        Query.AsNoTracking().Where(role => role.Id == roleId);
    }
}