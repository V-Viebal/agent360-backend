using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;

namespace Agent360.UserManagement.UseCase.Roles.Commands.Delete;

public sealed record DeleteRoleCommand(long Id, uint ExpectedRowVersion)
    : ICommand<Result>;