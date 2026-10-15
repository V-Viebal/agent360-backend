using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Agent360.UserManagement.UseCase.Roles.Common;
using Agent360.UserManagement.UseCase.Roles.Queries.Search;
using Microsoft.EntityFrameworkCore;

namespace Agent360.UserManagement.Infrastructure.Persistence.Queries;

/// <summary>
/// Feature adapter that supplies the role source query, projection, and allow-list configuration
/// to the shared advanced-search service.
/// </summary>
public sealed class RoleAdvancedSearchQuery(
    AppDbContext dbContext,
    IAdvancedSearchService advancedSearch)
    : IAdvancedSearchQuery<RoleResponse>
{
    /// <inheritdoc />
    public ValueTask<PagedResponse<RoleResponse>> ExecuteAsync(
        AdvancedSearchRequest request,
        CancellationToken cancellationToken = default) =>
        advancedSearch.ExecutePageAsync(
            dbContext.Roles.AsNoTracking(),
            role => new RoleResponse(
                role.Id,
                role.Name,
                role.Code,
                role.IsSystemRole,
                role.RowVersion),
            request,
            RoleAdvancedSearchConfiguration.Instance,
            cancellationToken: cancellationToken);
}