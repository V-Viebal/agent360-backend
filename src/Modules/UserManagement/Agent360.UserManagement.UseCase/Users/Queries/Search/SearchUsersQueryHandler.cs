using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Users.Common;

namespace Agent360.UserManagement.UseCase.Users.Queries.Search;

public sealed class SearchUsersQueryHandler(
    IAdvancedSearchQuery<UserResponse> advancedSearchQuery)
    : IQueryHandler<SearchUsersQuery, Result<PagedResponse<UserResponse>>>
{
    public async ValueTask<Result<PagedResponse<UserResponse>>> Handle(
        SearchUsersQuery query,
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
            return exception.ToValidationFailure<PagedResponse<UserResponse>>(
                UserApplicationErrorCodes.InvalidSearch);
        }
    }
}