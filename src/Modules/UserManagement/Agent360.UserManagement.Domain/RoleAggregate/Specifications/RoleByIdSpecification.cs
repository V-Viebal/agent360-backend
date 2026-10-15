using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.RoleAggregate.Specifications;

public sealed class RoleByIdSpecification : Specification<Role>
{
    public RoleByIdSpecification(long roleId)
    {
        Query.AsTracking().Where(role => role.Id == roleId);
    }
}