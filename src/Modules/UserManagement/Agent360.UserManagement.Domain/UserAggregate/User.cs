using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Data;
using Agent360.UserManagement.Domain.UserAggregate.Entities;
using Agent360.UserManagement.Domain.UserAggregate.Enums;
using Agent360.UserManagement.Domain.UserAggregate.Events;
using Agent360.UserManagement.Domain.UserAggregate.Exceptions;
using Agent360.UserManagement.Domain.UserAggregate.ValueObjects;

namespace Agent360.UserManagement.Domain.UserAggregate;

public sealed class User : SoftDeletableAuditableHasRowVersionEntityBase<User>, IAggregateRoot
{
    public string LogtoUserId { get; private set; } = string.Empty;

    public EmailAddress? Email { get; private set; }
    public PhoneNumber? PhoneNumber { get; private set; }

    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; } = string.Empty;

    public string? AvatarUrl { get; private set; }

    public long? PreferredCurrencyId { get; private set; }
    public string? PreferredLanguageCode { get; private set; }

    public UserStatus Status { get; private set; } = UserStatus.Active;
    public DateTimeOffset PermissionStamp { get; private set; } = DateTimeOffset.UtcNow;

    private readonly List<UserRole> _roles = [];
    public IReadOnlyList<UserRole> Roles => _roles.AsReadOnly();

    private User() { }

    #region Factory
    public static User Register(
        string logtoUserId,
        string email,
        long roleId,
        string firstName,
        string lastName,
        string? avatarUrl = null,
        long? preferredCurrencyId = null,
        string? preferredLanguageCode = null)
    {
        if (string.IsNullOrWhiteSpace(logtoUserId))
            throw new UserDomainException(
                UserErrorCodes.LogtoUserIdRequired,
                UserErrorMessages.LogtoUserIdRequired);

        var user = new User
        {
            LogtoUserId = logtoUserId.Trim(),
            Email = EmailAddress.Create(email),
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            DisplayName = $"{firstName.Trim()} {lastName.Trim()}".Trim(),
            AvatarUrl = avatarUrl,
            PreferredCurrencyId = preferredCurrencyId,
            PreferredLanguageCode = preferredLanguageCode,
            Status = UserStatus.Active
        };

        user.AssignRole(roleId);

        user.RegisterDomainEvent(new UserRegisteredDomainEvent
        {
            Id = user.Id,
            Email = user.Email.ToString(),
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            DisplayName = user.DisplayName
        }
       );

        return user;
    }

    public static User CreateSystemUser(string userId, string email, string firstName, string lastName)
    {
        var user = new User
        {
            LogtoUserId = userId,
            Email = EmailAddress.Create(email),
            FirstName = firstName,
            LastName = lastName,
            DisplayName = $"{firstName} {lastName}",
            Status = UserStatus.Active
        };
        return user;
    }
    #endregion

    #region Behaviors

    public void AssignRole(long roleId)
    {
        if (_roles.Any(x => x.RoleId == roleId))
            return;

        _roles.Add(new UserRole(Id, roleId));
        PermissionStamp = DateTimeOffset.UtcNow;
    }



    public void AssignRoles(IEnumerable<long> roleIds)
    {
        var changed = false;
        foreach (var roleId in roleIds)
        {
            if (!_roles.Any(x => x.RoleId == roleId))
            {
                _roles.Add(new UserRole(Id, roleId));
                changed = true;
            }
        }

        if (changed)
        {
            PermissionStamp = DateTimeOffset.UtcNow;
        }
    }

    public void SyncRoles(IEnumerable<long> roleIds)
    {
        var targetSet = roleIds.Distinct().ToHashSet();
        var currentSet = _roles.Select(x => x.RoleId).ToHashSet();

        if (targetSet.SetEquals(currentSet))
            return;

        _roles.RemoveAll(x => !targetSet.Contains(x.RoleId));
        foreach (var roleId in targetSet.Where(r => !currentSet.Contains(r)))
        {
            _roles.Add(new UserRole(Id, roleId));
        }

        PermissionStamp = DateTimeOffset.UtcNow;
    }

    public void RemoveRole(long roleId)
    {
        var role = _roles.FirstOrDefault(x => x.RoleId == roleId);
        if (role == null) return;

        _roles.Remove(role);
        PermissionStamp = DateTimeOffset.UtcNow;
    }

    #endregion
}