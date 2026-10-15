using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Exceptions;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Commands.Update;

public sealed class UpdateRoleCommandHandler(
    IRepository<Role> roleRepository,
    IUserManagementUnitOfWork unitOfWork,
    IRolePersistenceErrorClassifier persistenceErrors)
    : ICommandHandler<UpdateRoleCommand, Result<RoleResponse>>
{
    public async ValueTask<Result<RoleResponse>> Handle(
        UpdateRoleCommand command,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.FirstOrDefaultAsync(
            new RoleByIdSpecification(command.Id),
            cancellationToken);

        if (role is null)
        {
            return Result.Failure<RoleResponse>(RoleErrors.NotFound);
        }

        if (role.RowVersion != command.ExpectedRowVersion)
        {
            return Result.Failure<RoleResponse>(
                CommonErrors.ConcurrencyPreconditionFailed);
        }

        try
        {
            role.Update(command.Name, command.Code);
        }
        catch (RoleDomainException exception)
        {
            return exception.Code == RoleErrorCodes.SystemRoleImmutable
                ? Result.Failure<RoleResponse>(RoleErrors.SystemRoleImmutable)
                : Result.Failure<RoleResponse>(
                    RoleErrors.Invalid(exception.Code, exception.Message));
        }

        if (await roleRepository.AnyAsync(
            new RoleByCodeSpecification(role.Code, role.Id),
            cancellationToken))
        {
            return Result.Failure<RoleResponse>(RoleErrors.DuplicateCode);
        }

        try
        {
            await roleRepository.UpdateAsync(role, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
            when (persistenceErrors.IsConcurrencyConflict(exception))
        {
            return Result.Failure<RoleResponse>(
                CommonErrors.ConcurrencyPreconditionFailed);
        }
        catch (Exception exception)
            when (persistenceErrors.IsCodeUniqueViolation(exception))
        {
            return Result.Failure<RoleResponse>(RoleErrors.DuplicateCode);
        }

        return Result.Success(RoleResponse.From(role));
    }
}