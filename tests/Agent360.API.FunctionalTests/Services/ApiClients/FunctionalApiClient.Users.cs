using Hmz.Core.SharedKernel.AdvancedSearch;
using Agent360.API.Features.Users.ReplaceRoles;
using System.Net.Http.Json;

namespace Agent360.API.FunctionalTests.Services.ApiClients;

public sealed partial class FunctionalApiClient
{
    public Task<HttpResponseMessage> GetCurrentUserAsync(
        CancellationToken cancellationToken = default) =>
        Client.GetAsync("/api/v1/users/me", cancellationToken);

    public Task<HttpResponseMessage> SearchUsersAsync(
        AdvancedSearchRequest? request = null,
        CancellationToken cancellationToken = default) =>
        Client.PostAsJsonAsync(
            "/api/v1/users/search",
            request ?? new AdvancedSearchRequest(),
            cancellationToken);

    public Task<HttpResponseMessage> ReplaceUserRolesAsync(
        long userId,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default) =>
        Client.PutAsJsonAsync(
            $"/api/v1/users/{userId}/roles",
            new ReplaceUserRolesRequest { RoleIds = roleIds },
            cancellationToken);
}