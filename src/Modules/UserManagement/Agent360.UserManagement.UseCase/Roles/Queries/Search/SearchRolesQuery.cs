using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Hmz.Core.SharedKernel.Messaging;
using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.UserManagement.UseCase.Roles.Queries.Search;

/// <summary>
/// Runs the shared, allow-listed advanced-search pipeline for roles.
/// </summary>
public sealed record SearchRolesQuery(AdvancedSearchRequest Request)
    : IQuery<Result<PagedResponse<RoleResponse>>>;