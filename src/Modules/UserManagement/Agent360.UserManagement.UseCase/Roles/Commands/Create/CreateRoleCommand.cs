using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Commands.Create;

public sealed record CreateRoleCommand(
    string Name,
    string Code,
    bool RequireAbsent = false)
    : ICommand<Result<RoleResponse>>;