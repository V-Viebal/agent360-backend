using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Identity.Abstractions;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.Domain.UserAggregate.Specifications;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Agent360.UserManagement.UseCase.Users.Abstractions;
using Agent360.UserManagement.UseCase.Users.Common;

namespace Agent360.UserManagement.UseCase.Users.Commands.ReplaceRoles;

public sealed class ReplaceUserRolesCommandHandler(
    IRepository<User> userRepository,
    IReadRepository<Role> roleRepository,
    IUserManagementUnitOfWork unitOfWork,
    IPermissionCacheInvalidator permissionCacheInvalidator,
    IUserPersistenceErrorClassifier persistenceErrors,
    ICurrentUser currentUser)
    : ICommandHandler<ReplaceUserRolesCommand, Result<UserResponse>>
{
    public async ValueTask<Result<UserResponse>> Handle(
        ReplaceUserRolesCommand command,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId == command.UserId)
        {
            return Result.Failure<UserResponse>(UserErrors.CannotAssignOwnRoles);
        }

        var user = await userRepository.FirstOrDefaultAsync(
            new UserByIdWithRolesSpecification(command.UserId),
            cancellationToken);

        if (user is null)
        {
            return Result.Failure<UserResponse>(UserErrors.NotFound);
        }

        if (user.RowVersion != command.ExpectedRowVersion)
        {
            return Result.Failure<UserResponse>(
                CommonErrors.ConcurrencyPreconditionFailed);
        }

        var roleIds = command.RoleIds.Distinct().ToArray();
        var roles = roleIds.Length == 0
            ? []
            : await roleRepository.ListAsync(
                new RolesByIdsReadOnlySpecification(roleIds),
                cancellationToken);

        if (roles.Count != roleIds.Length)
        {
            return Result.Failure<UserResponse>(UserErrors.InvalidRoleIds);
        }

        if (roles.Any(role => role.IsSystemRole))
        {
            return Result.Failure<UserResponse>(
                UserErrors.SystemRoleAssignmentForbidden);
        }

        user.SyncRoles(roleIds);

        try
        {
            await userRepository.UpdateAsync(user, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
            when (persistenceErrors.IsConcurrencyConflict(exception))
        {
            return Result.Failure<UserResponse>(
                CommonErrors.ConcurrencyPreconditionFailed);
        }

        await permissionCacheInvalidator.InvalidateUserAsync(
            user.Id,
            cancellationToken);

        return Result.Success(UserResponse.From(
            user,
            roles.ToDictionary(role => role.Id)));
    }
}