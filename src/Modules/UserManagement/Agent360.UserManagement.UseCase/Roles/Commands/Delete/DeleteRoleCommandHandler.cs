using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Exceptions;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Abstractions;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Commands.Delete;

public sealed class DeleteRoleCommandHandler(
    IRepository<Role> roleRepository,
    IUserManagementUnitOfWork unitOfWork,
    IRolePersistenceErrorClassifier persistenceErrors)
    : ICommandHandler<DeleteRoleCommand, Result>
{
    public async ValueTask<Result> Handle(
        DeleteRoleCommand command,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.FirstOrDefaultAsync(
            new RoleByIdSpecification(command.Id),
            cancellationToken);

        if (role is null)
        {
            return Result.Failure(RoleErrors.NotFound);
        }

        if (role.RowVersion != command.ExpectedRowVersion)
        {
            return Result.Failure(CommonErrors.ConcurrencyPreconditionFailed);
        }

        try
        {
            role.EnsureCanBeDeleted();
        }
        catch (RoleDomainException exception)
        {
            return exception.Code == RoleErrorCodes.SystemRoleImmutable
                ? Result.Failure(RoleErrors.SystemRoleImmutable)
                : Result.Failure(
                    RoleErrors.Invalid(exception.Code, exception.Message));
        }

        try
        {
            await roleRepository.DeleteAsync(role, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
            when (persistenceErrors.IsConcurrencyConflict(exception))
        {
            return Result.Failure(CommonErrors.ConcurrencyPreconditionFailed);
        }

        return Result.Success();
    }
}