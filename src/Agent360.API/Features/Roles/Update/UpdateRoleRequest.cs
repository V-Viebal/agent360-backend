using Agent360.UserManagement.Domain.RoleAggregate;
using System.ComponentModel.DataAnnotations;

namespace Agent360.API.Features.Roles.Update;

public sealed class UpdateRoleRequest
{
    [Required]
    [StringLength(Role.MaxNameLength)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [StringLength(Role.MaxCodeLength)]
    public string Code { get; init; } = string.Empty;
}