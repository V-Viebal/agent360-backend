using Ardalis.Specification;

namespace Agent360.UserManagement.Domain.UserAggregate.Specifications;

public sealed class UserExistsSpecification : Specification<User>
{
    public UserExistsSpecification(string logtoUserId, string email)
    {
        var normalizedLogtoUserId = logtoUserId.Trim();
        var normalizedEmail = email.Trim().ToLowerInvariant();

        Query.AsNoTracking().Where(user =>
            user.LogtoUserId == normalizedLogtoUserId ||
            user.Email!.Value == normalizedEmail);
    }
}