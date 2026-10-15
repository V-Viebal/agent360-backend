using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.UserAggregate.Specifications;

public sealed class UserByIdWithRolesSpecification : SingleResultSpecification<User>
{
    public UserByIdWithRolesSpecification(long userId)
    {
        Query
            .AsTracking()
            .Where(user => user.Id == userId)
            .Include(user => user.Roles);
    }
}