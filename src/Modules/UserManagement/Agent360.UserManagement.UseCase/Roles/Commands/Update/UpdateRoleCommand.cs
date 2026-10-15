using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Commands.Update;

public sealed record UpdateRoleCommand(
    long Id,
    string Name,
    string Code,
    uint ExpectedRowVersion)
    : ICommand<Result<RoleResponse>>;