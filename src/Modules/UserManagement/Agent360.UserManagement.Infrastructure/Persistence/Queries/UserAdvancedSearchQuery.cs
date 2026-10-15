using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Agent360.UserManagement.Domain.RoleAggregate;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.UseCase.Users.Common;
using Agent360.UserManagement.UseCase.Users.Queries.Search;
using Microsoft.EntityFrameworkCore;

namespace Agent360.UserManagement.Infrastructure.Persistence.Queries;

/// <summary>
/// Selects the user page in PostgreSQL before hydrating its role metadata.
/// </summary>
public sealed class UserAdvancedSearchQuery(
    AppDbContext dbContext,
    IAdvancedSearchService advancedSearch)
    : IAdvancedSearchQuery<UserResponse>
{
    public async ValueTask<PagedResponse<UserResponse>> ExecuteAsync(
        AdvancedSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await advancedSearch.ExecutePageAsync(
            dbContext.Users
                .AsNoTracking()
                .Include(user => user.Roles),
            request,
            UserAdvancedSearchConfiguration.Instance,
            cancellationToken: cancellationToken);

        var roleIds = page.Items
            .SelectMany(user => user.Roles)
            .Select(userRole => userRole.RoleId)
            .Distinct()
            .ToArray();

        var rolesById = roleIds.Length == 0
            ? new Dictionary<long, Role>()
            : await dbContext.Roles
                .AsNoTracking()
                .Where(role => roleIds.Contains(role.Id))
                .ToDictionaryAsync(role => role.Id, cancellationToken);

        return new PagedResponse<UserResponse>(
            page.Items.Select(user => UserResponse.From(user, rolesById)).ToArray(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }
}