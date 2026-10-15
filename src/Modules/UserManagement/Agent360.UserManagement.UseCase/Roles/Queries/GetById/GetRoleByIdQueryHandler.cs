using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Queries.GetById;

public sealed class GetRoleByIdQueryHandler(IReadRepository<Role> roleRepository)
    : IQueryHandler<GetRoleByIdQuery, Result<RoleResponse>>
{
    public async ValueTask<Result<RoleResponse>> Handle(
        GetRoleByIdQuery query,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.FirstOrDefaultAsync(
            new RoleByIdReadOnlySpecification(query.Id),
            cancellationToken);

        return role is null
            ? Result.Failure<RoleResponse>(RoleErrors.NotFound)
            : Result.Success(RoleResponse.From(role));
    }
}