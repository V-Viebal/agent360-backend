using Agent360.API.Features.Permissions.ReplaceRole;
using System.Net.Http.Json;

namespace Agent360.API.FunctionalTests.Services.ApiClients;

public sealed partial class FunctionalApiClient
{
    public Task<HttpResponseMessage> ListPermissionsAsync(
        CancellationToken cancellationToken = default) =>
        Client.GetAsync("/api/v1/permissions", cancellationToken);

    public Task<HttpResponseMessage> GetRolePermissionsAsync(
        long roleId,
        CancellationToken cancellationToken = default) =>
        Client.GetAsync($"/api/v1/roles/{roleId}/permissions", cancellationToken);

    public Task<HttpResponseMessage> ReplaceRolePermissionsAsync(
        long roleId,
        IReadOnlyList<long> permissionIds,
        CancellationToken cancellationToken = default) =>
        Client.PutAsJsonAsync(
            $"/api/v1/roles/{roleId}/permissions",
            new ReplaceRolePermissionsRequest { PermissionIds = permissionIds },
            cancellationToken);
}