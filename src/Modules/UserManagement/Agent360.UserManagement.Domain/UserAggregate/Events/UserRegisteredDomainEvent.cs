using Ardalis.SharedKernel;

namespace Agent360.UserManagement.Domain.UserAggregate.Events;

public sealed class UserRegisteredDomainEvent : DomainEventBase
{
    public long Id { get; init; }

    #region Optional properties for snapshotting,...

    public string Email { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    #endregion
}