using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Exceptions;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Commands.Create;

public sealed class CreateRoleCommandHandler(
    IRepository<Role> roleRepository,
    IUserManagementUnitOfWork unitOfWork,
    IRolePersistenceErrorClassifier persistenceErrors)
    : ICommandHandler<CreateRoleCommand, Result<RoleResponse>>
{
    public async ValueTask<Result<RoleResponse>> Handle(
        CreateRoleCommand command,
        CancellationToken cancellationToken)
    {
        Role role;

        try
        {
            role = Role.Create(command.Name, command.Code);
        }
        catch (RoleDomainException exception)
        {
            return Result.Failure<RoleResponse>(
                RoleErrors.Invalid(exception.Code, exception.Message));
        }

        if (await roleRepository.AnyAsync(
            new RoleByCodeSpecification(role.Code),
            cancellationToken))
        {
            return command.RequireAbsent
                ? Result.Failure<RoleResponse>(
                    CommonErrors.ConcurrencyPreconditionFailed)
                : Result.Failure<RoleResponse>(RoleErrors.DuplicateCode);
        }

        try
        {
            await roleRepository.AddAsync(role, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
            when (persistenceErrors.IsCodeUniqueViolation(exception))
        {
            return command.RequireAbsent
                ? Result.Failure<RoleResponse>(
                    CommonErrors.ConcurrencyPreconditionFailed)
                : Result.Failure<RoleResponse>(RoleErrors.DuplicateCode);
        }

        return Result.Success(RoleResponse.From(role));
    }
}