using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Users.Common;

namespace Agent360.UserManagement.UseCase.Users.Queries.Search;

public sealed record SearchUsersQuery(AdvancedSearchRequest Request)
    : IQuery<Result<PagedResponse<UserResponse>>>;