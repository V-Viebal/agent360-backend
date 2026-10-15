using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.Domain.PermissionAggregate;
using Agent360.UserManagement.Domain.PermissionAggregate.Specifications;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Exceptions;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Common;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Permissions.Commands.ReplaceRole;

public sealed class ReplaceRolePermissionsCommandHandler(
    IRepository<Role> roleRepository,
    IReadRepository<Permission> permissionRepository,
    IUserManagementUnitOfWork unitOfWork,
    IPermissionCacheInvalidator cacheInvalidator)
    : ICommandHandler<ReplaceRolePermissionsCommand, Result<RolePermissionsResponse>>
{
    public async ValueTask<Result<RolePermissionsResponse>> Handle(
        ReplaceRolePermissionsCommand command,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.FirstOrDefaultAsync(
            new RoleByIdWithPermissionsSpecification(command.RoleId),
            cancellationToken);

        if (role is null)
            return Result.Failure<RolePermissionsResponse>(RoleErrors.NotFound);

        if (role.RowVersion != command.ExpectedRowVersion)
            return Result.Failure<RolePermissionsResponse>(
                CommonErrors.ConcurrencyPreconditionFailed);

        var permissionIds = command.PermissionIds.Distinct().ToArray();
        var permissions = permissionIds.Length == 0
            ? []
            : await permissionRepository.ListAsync(
                new PermissionsByIdsReadOnlySpecification(permissionIds),
                cancellationToken);

        if (permissions.Count != permissionIds.Length)
            return Result.Failure<RolePermissionsResponse>(RoleErrors.InvalidPermissionIds);

        var includesUserRoleManagement = permissions.Any(permission =>
            permission.Code == UserManagementPermissions.Users.ManageRoles);

        if (role.Code != RoleCodes.Admin && includesUserRoleManagement)
        {
            return Result.Failure<RolePermissionsResponse>(
                RoleErrors.AdminOnlyPermission);
        }

        if (role.Code == RoleCodes.Admin && !includesUserRoleManagement)
        {
            return Result.Failure<RolePermissionsResponse>(
                RoleErrors.RequiredAdminPermission);
        }

        try
        {
            role.ReplacePermissions(permissionIds);
        }
        catch (RoleDomainException exception)
        {
            return exception.Code == RoleErrorCodes.SystemRoleImmutable
                ? Result.Failure<RolePermissionsResponse>(RoleErrors.SystemRoleImmutable)
                : Result.Failure<RolePermissionsResponse>(
                    RoleErrors.Invalid(exception.Code, exception.Message));
        }

        await roleRepository.UpdateAsync(role, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cacheInvalidator.InvalidateRoleAsync(role.Id, cancellationToken);

        return Result.Success(new RolePermissionsResponse(
            role.Id,
            role.RowVersion,
            permissions
                .OrderBy(permission => permission.Code, StringComparer.Ordinal)
                .Select(permission => new PermissionMetadataResponse(
                    permission.Id,
                    permission.Code,
                    permission.Name,
                    permission.Description))
                .ToArray()));
    }
}