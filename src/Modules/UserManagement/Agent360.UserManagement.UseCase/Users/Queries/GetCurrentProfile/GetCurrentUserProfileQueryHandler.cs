using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Authorization;
using Hmz.Core.SharedKernel.Messaging;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.Domain.UserAggregate.Specifications;

namespace Agent360.UserManagement.UseCase.Users.Queries.GetCurrentProfile;

public sealed class GetCurrentUserProfileQueryHandler(
    IReadRepository<User> userRepository,
    IReadRepository<Role> roleRepository,
    IPermissionSnapshotAccessor permissionSnapshotAccessor)
    : IQueryHandler<GetCurrentUserProfileQuery, CurrentUserProfileResponse?>
{
    public async ValueTask<CurrentUserProfileResponse?> Handle(
        GetCurrentUserProfileQuery query,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.SingleOrDefaultAsync(
            new UserByIdReadOnlySpecification(query.UserId),
            cancellationToken);

        if (user is null)
        {
            return null;
        }

        var roleIds = user.Roles
            .Select(userRole => userRole.RoleId)
            .Distinct()
            .ToArray();

        IReadOnlyList<Role> roles = roleIds.Length == 0
            ? []
            : await roleRepository.ListAsync(
                new RolesByIdsReadOnlySpecification(roleIds),
                cancellationToken);

        var phoneNumber = user.PhoneNumber is null
            ? null
            : new CurrentUserPhoneNumberResponse(
                user.PhoneNumber.CountryCode,
                user.PhoneNumber.Number,
                user.PhoneNumber.Extension);

        return new CurrentUserProfileResponse(
            user.Id,
            user.LogtoUserId,
            user.Email?.Value,
            phoneNumber,
            user.FirstName,
            user.LastName,
            user.DisplayName,
            user.AvatarUrl,
            user.PreferredCurrencyId,
            user.PreferredLanguageCode,
            user.Status.Name,
            user.RowVersion,
            roles.Select(role => new CurrentUserRoleResponse(
                    role.Id,
                    role.Code,
                    role.Name))
                .ToArray(),
            permissionSnapshotAccessor.Snapshot.Permissions.Order(StringComparer.Ordinal).ToArray());
    }
}