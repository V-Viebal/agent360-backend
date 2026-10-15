using Agent360.UserManagement.Domain.UserAggregate.Events;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Agent360.UserManagement.Domain.UserAggregate.Handlers;

public sealed class UserRegisteredDomainEventHandler(ILogger<UserRegisteredDomainEventHandler> logger) : INotificationHandler<UserRegisteredDomainEvent>
{
    public ValueTask Handle(UserRegisteredDomainEvent notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling UserRegisteredDomainEvent for user with ID {UserId} and email {Email}", notification.Id, notification.Email);
        // Perform any necessary actions in response to the user registration event, such as sending a welcome email or updating related data.
        //throw new NotImplementedException();
        return ValueTask.CompletedTask;
    }
}