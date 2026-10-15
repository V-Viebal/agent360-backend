using Hmz.Core.SharedKernel.Messaging;
using Agent360.UserManagement.UseCase.Permissions.Common;

namespace Agent360.UserManagement.UseCase.Permissions.Queries.List;

public sealed record ListPermissionsQuery : IQuery<PermissionCatalogResponse>;