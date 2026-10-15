using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Queries.GetById;

public sealed record GetRoleByIdQuery(long Id) : IQuery<Result<RoleResponse>>;