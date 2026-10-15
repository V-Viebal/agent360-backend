using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Messaging;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.RoleAggregate.Specifications;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.Domain.UserAggregate.Specifications;
using Agent360.UserManagement.UseCase.Abstractions;

namespace Agent360.UserManagement.UseCase.Users.Commands.CreateByLogtoWebhook;

public sealed class CreateByLogtoWebhookCommandHandler(
    IRepository<User> userRepository,
    IRepository<Role> roleRepository,
    IUserManagementUnitOfWork unitOfWork,
    IUserIdentityService userIdentityService)
    : ICommandHandler<CreateByLogtoWebhookCommand>
{
    public async ValueTask<Mediator.Unit> Handle(
        CreateByLogtoWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var userExistsSpecification = new UserExistsSpecification(
            command.LogtoUserId,
            command.Email);

        if (await userRepository.AnyAsync(
            userExistsSpecification,
            cancellationToken))
        {
            await userIdentityService.InvalidateAsync(
                command.LogtoUserId,
                cancellationToken);
            return Mediator.Unit.Value;
        }

        var customerRole = await roleRepository.FirstOrDefaultAsync(
            new RoleByCodeSpecification(RoleCodes.Customer),
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Role '{RoleCodes.Customer}' has not been seeded.");

        var user = User.Register(
            command.LogtoUserId,
            command.Email,
            customerRole.Id,
            command.FirstName,
            command.LastName,
            command.AvatarUrl);

        await userRepository.AddAsync(user, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await userIdentityService.InvalidateAsync(
            command.LogtoUserId,
            cancellationToken);

        return Mediator.Unit.Value;
    }
}