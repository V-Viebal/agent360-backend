using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.RoleAggregate.Specifications;

public sealed class RoleByCodeSpecification : Specification<Role>
{
    public RoleByCodeSpecification(string roleCode, long? excludedRoleId = null)
    {
        var normalizedRoleCode = roleCode.Trim().ToUpperInvariant();

        Query.AsNoTracking().Where(role =>
            role.Code == normalizedRoleCode &&
            (!excludedRoleId.HasValue || role.Id != excludedRoleId.Value));
    }
}