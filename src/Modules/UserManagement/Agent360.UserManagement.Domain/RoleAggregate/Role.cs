using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Data;
using Agent360.UserManagement.Domain.RoleAggregate.Entities;
using Agent360.UserManagement.Domain.RoleAggregate.Exceptions;

namespace Agent360.UserManagement.Domain.RoleAggregate;

public sealed class Role : SoftDeletableAuditableHasRowVersionEntityBase<Role>, IAggregateRoot
{
    public const int MaxNameLength = 100;
    public const int MaxCodeLength = 50;

    public string Name { get; private set; } = default!;
    public string Code { get; private set; } = default!;
    public bool IsSystemRole { get; private set; }
    public DateTimeOffset PermissionStamp { get; private set; } = DateTimeOffset.UtcNow;

    private readonly List<RolePermission> _permissions = [];
    public IReadOnlyList<RolePermission> Permissions => _permissions.AsReadOnly();

    private Role() { }

    public static Role Create(string name, string code, bool isSystemRole = false)
    {
        return new Role
        {
            Name = NormalizeName(name),
            Code = NormalizeCode(code),
            IsSystemRole = isSystemRole
        };
    }

    public void Rename(string name)
    {
        EnsureMutable();
        Name = NormalizeName(name);
    }

    public void Update(string name, string code)
    {
        EnsureMutable();

        Name = NormalizeName(name);
        Code = NormalizeCode(code);
    }

    public void ReplacePermissions(IEnumerable<long> permissionIds)
    {
        EnsureMutable();
        ReplacePermissionIds(permissionIds);
    }

    public void SeedPermissions(IEnumerable<long> permissionIds)
    {
        ReplacePermissionIds(permissionIds);
    }

    private void ReplacePermissionIds(IEnumerable<long> permissionIds)
    {
        var ids = permissionIds.Distinct().ToHashSet();

        if (_permissions.Count == ids.Count
            && _permissions.All(permission => ids.Contains(permission.PermissionId)))
        {
            return;
        }

        _permissions.RemoveAll(x => !ids.Contains(x.PermissionId));
        foreach (var id in ids.Where(id => _permissions.All(x => x.PermissionId != id)))
            _permissions.Add(new RolePermission(Id, id));
        PermissionStamp = DateTimeOffset.UtcNow;
    }

    public void EnsureCanBeDeleted()
    {
        EnsureMutable();
    }

    public static string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new RoleDomainException(
                RoleErrorCodes.CodeRequired,
                RoleErrorMessages.CodeRequired);
        }

        var normalizedCode = code.Trim().ToUpperInvariant();

        if (normalizedCode.Length > MaxCodeLength)
        {
            throw new RoleDomainException(
                RoleErrorCodes.CodeTooLong,
                string.Format(RoleErrorMessages.CodeTooLong, MaxCodeLength));
        }

        return normalizedCode;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new RoleDomainException(
                RoleErrorCodes.NameRequired,
                RoleErrorMessages.NameRequired);
        }

        var normalizedName = name.Trim();

        if (normalizedName.Length > MaxNameLength)
        {
            throw new RoleDomainException(
                RoleErrorCodes.NameTooLong,
                string.Format(RoleErrorMessages.NameTooLong, MaxNameLength));
        }

        return normalizedName;
    }

    private void EnsureMutable()
    {
        if (IsSystemRole)
        {
            throw new RoleDomainException(
                RoleErrorCodes.SystemRoleImmutable,
                RoleErrorMessages.SystemRoleImmutable);
        }
    }
}