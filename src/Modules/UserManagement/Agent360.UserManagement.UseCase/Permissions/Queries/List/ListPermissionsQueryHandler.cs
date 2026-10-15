using Hmz.Core.SharedKernel.Messaging;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Common;

namespace Agent360.UserManagement.UseCase.Permissions.Queries.List;

public sealed class ListPermissionsQueryHandler(IPermissionQueryService queryService)
    : IQueryHandler<ListPermissionsQuery, PermissionCatalogResponse>
{
    public async ValueTask<PermissionCatalogResponse> Handle(
        ListPermissionsQuery query,
        CancellationToken cancellationToken) =>
        await queryService.ListAsync(cancellationToken);
}