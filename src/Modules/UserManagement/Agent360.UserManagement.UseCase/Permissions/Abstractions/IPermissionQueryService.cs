using Agent360.UserManagement.UseCase.Permissions.Common;

namespace Agent360.UserManagement.UseCase.Permissions.Abstractions;

public interface IPermissionQueryService
{
    Task<PermissionCatalogResponse> ListAsync(CancellationToken cancellationToken = default);

    Task<RolePermissionsResponse?> GetRolePermissionsAsync(
        long roleId,
        CancellationToken cancellationToken = default);
}