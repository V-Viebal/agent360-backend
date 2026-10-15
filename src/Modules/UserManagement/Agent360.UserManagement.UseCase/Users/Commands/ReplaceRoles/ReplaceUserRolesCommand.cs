using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Users.Common;

namespace Agent360.UserManagement.UseCase.Users.Commands.ReplaceRoles;

public sealed record ReplaceUserRolesCommand(
    long UserId,
    IReadOnlyList<long> RoleIds,
    uint ExpectedRowVersion)
    : ICommand<Result<UserResponse>>;