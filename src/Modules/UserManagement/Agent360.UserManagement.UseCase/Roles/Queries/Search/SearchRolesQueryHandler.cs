using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Queries.Search;

/// <summary>
/// Delegates the role list request to the generic advanced-search adapter.
/// </summary>
public sealed class SearchRolesQueryHandler(
    IAdvancedSearchQuery<RoleResponse> advancedSearchQuery)
    : IQueryHandler<SearchRolesQuery, Result<PagedResponse<RoleResponse>>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PagedResponse<RoleResponse>>> Handle(
        SearchRolesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await advancedSearchQuery.ExecuteAsync(
                query.Request,
                cancellationToken);

            return Result.Success(response);
        }
        catch (AdvancedSearchValidationException exception)
        {
            return exception.ToValidationFailure<PagedResponse<RoleResponse>>(
                RoleApplicationErrorCodes.InvalidSearch);
        }
    }
}