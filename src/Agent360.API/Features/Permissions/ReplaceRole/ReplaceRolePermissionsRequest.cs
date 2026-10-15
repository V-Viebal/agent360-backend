using System.ComponentModel.DataAnnotations;

namespace Agent360.API.Features.Permissions.ReplaceRole;

public sealed class ReplaceRolePermissionsRequest
{
    [Required]
    public IReadOnlyList<long> PermissionIds { get; init; } = [];
}