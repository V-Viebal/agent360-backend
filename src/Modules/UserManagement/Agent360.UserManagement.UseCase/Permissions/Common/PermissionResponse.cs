namespace Agent360.UserManagement.UseCase.Permissions.Common;

public sealed record PermissionResponse(
    long Id,
    string Code,
    string Name,
    string? Description,
    IReadOnlyList<PermissionRoleResponse> Roles);

public sealed record PermissionCatalogResponse(
    IReadOnlyList<PermissionResponse> Permissions,
    string ETag);

public sealed record PermissionMetadataResponse(
    long Id,
    string Code,
    string Name,
    string? Description);

public sealed record PermissionRoleResponse(
    long Id,
    string Code,
    string Name);

public sealed record RolePermissionsResponse(
    long RoleId,
    uint RowVersion,
    IReadOnlyList<PermissionMetadataResponse> Permissions);