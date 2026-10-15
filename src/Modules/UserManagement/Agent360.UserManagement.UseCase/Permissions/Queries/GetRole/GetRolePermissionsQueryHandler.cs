using Hmz.Core.SharedKernel.Messaging;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Common;

namespace Agent360.UserManagement.UseCase.Permissions.Queries.GetRole;

public sealed class GetRolePermissionsQueryHandler(IPermissionQueryService queryService)
    : IQueryHandler<GetRolePermissionsQuery, RolePermissionsResponse?>
{
    public async ValueTask<RolePermissionsResponse?> Handle(
        GetRolePermissionsQuery query,
        CancellationToken cancellationToken) =>
        await queryService.GetRolePermissionsAsync(query.RoleId, cancellationToken);
}