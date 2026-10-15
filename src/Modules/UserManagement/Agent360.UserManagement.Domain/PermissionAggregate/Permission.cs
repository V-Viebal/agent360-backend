using Ardalis.SharedKernel;
using Hmz.Core.SharedKernel.Authorization;
using Hmz.Core.SharedKernel.Data;

namespace Agent360.UserManagement.Domain.PermissionAggregate;

public sealed class Permission : SoftDeletableAuditableEntityBase<Permission>, IAggregateRoot
{
    public const int MaxCodeLength = 200;
    public const int MaxNameLength = 150;

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public IReadOnlyList<RoleAggregate.Entities.RolePermission> RolePermissions =>
        _rolePermissions.AsReadOnly();

    private readonly List<RoleAggregate.Entities.RolePermission> _rolePermissions = [];
    private Permission() { }

    public static Permission Create(string code, string name, string? description = null) => new()
    {
        Code = NormalizeCode(code),
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Permission name is required.", nameof(name)) : name.Trim(),
        Description = description?.Trim()
    };

    private static string NormalizeCode(string code)
    {
        var normalized = PermissionCode.Normalize(code);
        if (!PermissionCode.IsValid(normalized))
            throw new ArgumentException("Permission code is invalid.", nameof(code));

        return normalized;
    }
}