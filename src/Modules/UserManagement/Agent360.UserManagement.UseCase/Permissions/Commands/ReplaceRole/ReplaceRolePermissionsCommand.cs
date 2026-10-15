using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Permissions.Common;

namespace Agent360.UserManagement.UseCase.Permissions.Commands.ReplaceRole;

public sealed record ReplaceRolePermissionsCommand(
    long RoleId,
    IReadOnlyList<long> PermissionIds,
    uint ExpectedRowVersion)
    : ICommand<Result<RolePermissionsResponse>>;