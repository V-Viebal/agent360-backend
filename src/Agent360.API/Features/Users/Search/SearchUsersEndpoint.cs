using Asp.Versioning;
using Hmz.Core.SharedKernel.AdvancedSearch;
using Hmz.Core.SharedKernel.Contracts.Pagination;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.UseCase.Users.Common;
using Agent360.UserManagement.UseCase.Users.Queries.Search;
using Microsoft.AspNetCore.Http.Metadata;

namespace Agent360.API.Features.Users.Search;

public sealed class SearchUsersEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost(
                "users/search",
                async Task<IResult> (
                    AdvancedSearchRequest? request,
                    HttpContext context,
                    ISender sender,
                    CancellationToken cancellationToken) =>
                {
                    var result = await sender.Send(
                        new SearchUsersQuery(request ?? new AdvancedSearchRequest()),
                        cancellationToken);

                    return result.IsFailure
                        ? result.ToProblem(context)
                        : TypedResults.Ok(result.Value);
                })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SearchUsers")
            .WithTags(EndpointTags.Users)
            .WithSummary("Search, filter, sort, and paginate users with their roles")
            .WithDescription(
                "Uses an explicit user field allow list. Role assignments are included in each " +
                "result but are not exposed as raw Dynamic LINQ fields.")
            .Accepts<AdvancedSearchRequest>(true, "application/json")
            .Produces<PagedResponse<UserResponse>>()
            .ProducesValidationProblem()
            .AddOpenApiOperationTransformer((operation, context, cancellationToken) =>
            {
                SearchUsersOpenApi.Apply(operation);
                return Task.CompletedTask;
            })
            .RequirePermission(UserManagementPermissions.Users.Read);
    }
}