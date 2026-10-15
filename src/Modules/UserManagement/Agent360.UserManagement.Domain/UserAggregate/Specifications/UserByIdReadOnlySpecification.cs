using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.UserAggregate.Specifications;

public sealed class UserByIdReadOnlySpecification : SingleResultSpecification<User>
{
    public UserByIdReadOnlySpecification(long userId)
    {
        Query
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Include(user => user.Roles);
    }
}