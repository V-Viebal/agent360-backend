using Asp.Versioning;
using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.UseCase.Roles.Common;
using Agent360.UserManagement.UseCase.Roles.Queries.Search;
using Microsoft.AspNetCore.Http.Metadata;

namespace Agent360.API.Features.Roles.Search;

/// <summary>
/// Demonstrates the shared, config-driven advanced-search contract on the role aggregate.
/// </summary>
public sealed class SearchRolesEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost(
            "roles/search",
            async Task<IResult> (
                AdvancedSearchRequest? request,
                HttpContext context,
                ISender sender,
                CancellationToken cancellationToken) =>
            {
                var result = await sender.Send(
                    new SearchRolesQuery(request ?? new AdvancedSearchRequest()),
                    cancellationToken);

                return result.IsFailure
                    ? result.ToProblem(context)
                    : TypedResults.Ok(result.Value);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SearchRoles")
            .WithTags(EndpointTags.Roles)
            .WithSummary("Search, filter, sort, and paginate roles")
            .WithDescription(
                "Template sample for Hmz.Core.SharedKernel.AdvancedSearch. " +
                "Only fields declared by RoleAdvancedSearchConfiguration are accepted: " +
                "global search uses Id, Name, and Code; filters and sorting also support IsSystemRole.")
            .Accepts<AdvancedSearchRequest>(true, "application/json")
            .Produces<PagedResponse<RoleResponse>>()
            .ProducesValidationProblem()
            .AddOpenApiOperationTransformer((operation, context, cancellationToken) =>
            {
                SearchRolesOpenApi.Apply(operation);
                return Task.CompletedTask;
            })
            .RequirePermission(Agent360.UserManagement.Domain.Constants.UserManagementPermissions.Roles.Read);
    }
}