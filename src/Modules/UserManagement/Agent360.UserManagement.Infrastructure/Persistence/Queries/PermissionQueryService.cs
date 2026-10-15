using Agent360.UserManagement.Domain.PermissionAggregate;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Agent360.UserManagement.UseCase.Permissions.Common;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Agent360.UserManagement.Infrastructure.Persistence.Queries;

public sealed class PermissionQueryService(AppDbContext context) : IPermissionQueryService
{
    public async Task<PermissionCatalogResponse> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var permissions = await context.Permissions
            .AsNoTracking()
            .Include(permission => permission.RolePermissions)
            .ThenInclude(rolePermission => rolePermission.Role)
            .OrderBy(permission => permission.Code)
            .ToArrayAsync(cancellationToken);

        var responses = permissions
            .Select(ToResponse)
            .ToArray();

        var versionParts = new List<string>();
        foreach (var permission in permissions)
        {
            versionParts.Add(
                $"{permission.Id}|{permission.Code}|{permission.Name}|{permission.Description}");
            versionParts.AddRange(permission.RolePermissions
                .OrderBy(rolePermission => rolePermission.RoleId)
                .Select(rolePermission =>
                    $"{rolePermission.RoleId}|{rolePermission.Role.Code}|{rolePermission.Role.Name}|{rolePermission.Role.PermissionStamp.UtcTicks}"));
        }

        var versionSource = string.Join("\n", versionParts);

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(versionSource)));

        return new PermissionCatalogResponse(
            responses,
            $"\"permissions-{digest}\"");
    }

    public async Task<RolePermissionsResponse?> GetRolePermissionsAsync(
        long roleId,
        CancellationToken cancellationToken = default)
    {
        var role = await context.Roles
            .AsNoTracking()
            .Include(item => item.Permissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(item => item.Id == roleId, cancellationToken);

        return role is null
            ? null
            : new RolePermissionsResponse(
                role.Id,
                role.RowVersion,
                role.Permissions
                    .OrderBy(item => item.Permission.Code)
                    .Select(item => ToMetadataResponse(item.Permission))
                    .ToArray());
    }

    private static PermissionResponse ToResponse(Permission permission) =>
        new(
            permission.Id,
            permission.Code,
            permission.Name,
            permission.Description,
            permission.RolePermissions
                .OrderBy(item => item.Role.Code)
                .Select(item => new PermissionRoleResponse(
                    item.Role.Id,
                    item.Role.Code,
                    item.Role.Name))
                .ToArray());

    private static PermissionMetadataResponse ToMetadataResponse(
        Permission permission) =>
        new(
            permission.Id,
            permission.Code,
            permission.Name,
            permission.Description);
}