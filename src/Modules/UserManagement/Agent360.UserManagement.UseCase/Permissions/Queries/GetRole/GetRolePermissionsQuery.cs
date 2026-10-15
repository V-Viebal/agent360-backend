using Hmz.Core.SharedKernel.Messaging;
using Agent360.UserManagement.UseCase.Permissions.Common;

namespace Agent360.UserManagement.UseCase.Permissions.Queries.GetRole;

public sealed record GetRolePermissionsQuery(long RoleId)
    : IQuery<RolePermissionsResponse?>;