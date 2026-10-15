using Hmz.Core.SharedKernel.AdvancedSearch;
using Agent360.API.Features.Roles.Create;
using Agent360.API.Features.Roles.Update;
using Agent360.UserManagement.UseCase.Roles.Common;
using System.Net;
using System.Net.Http.Json;

namespace Agent360.API.FunctionalTests.Services.ApiClients;

public sealed partial class FunctionalApiClient
{
    public Task<HttpResponseMessage> SearchRolesAsync(
        AdvancedSearchRequest? request = null,
        CancellationToken cancellationToken = default) =>
        Client.PostAsJsonAsync(
            "/api/v1/roles/search",
            request ?? new AdvancedSearchRequest(),
            cancellationToken);

    public Task<HttpResponseMessage> CreateRoleAsync(
        string name,
        string code,
        CancellationToken cancellationToken = default) =>
        Client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleRequest { Name = name, Code = code },
            cancellationToken);

    public Task<HttpResponseMessage> GetRoleAsync(
        long roleId,
        CancellationToken cancellationToken = default) =>
        Client.GetAsync($"/api/v1/roles/{roleId}", cancellationToken);

    public Task<HttpResponseMessage> UpdateRoleAsync(
        long roleId,
        string name,
        string code,
        CancellationToken cancellationToken = default) =>
        Client.PutAsJsonAsync(
            $"/api/v1/roles/{roleId}",
            new UpdateRoleRequest { Name = name, Code = code },
            cancellationToken);

    public Task<HttpResponseMessage> DeleteRoleAsync(
        long roleId,
        CancellationToken cancellationToken = default) =>
        Client.DeleteAsync($"/api/v1/roles/{roleId}", cancellationToken);

    public async Task<RoleResponse> CreateRoleAndReadAsync(
        string name,
        string code,
        CancellationToken cancellationToken = default)
    {
        using var response = await CreateRoleAsync(name, code, cancellationToken);
        return await response.ReadRequiredJsonAsync<RoleResponse>(
            HttpStatusCode.Created,
            cancellationToken: cancellationToken);
    }
}