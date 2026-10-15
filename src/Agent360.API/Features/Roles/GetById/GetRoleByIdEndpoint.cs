using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.UseCase.Roles.Common;
using Agent360.UserManagement.UseCase.Roles.Queries.GetById;

namespace Agent360.API.Features.Roles.GetById;

public sealed class GetRoleByIdEndpoint : IEndpoint
{
    public const string RouteName = "GetRoleById";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "roles/{id:long}",
            async Task<Microsoft.AspNetCore.Http.IResult> (
                long id,
                HttpContext context,
                ISender sender,
                CancellationToken cancellationToken) =>
            {
                var result = await sender.Send(
                    new GetRoleByIdQuery(id),
                    cancellationToken);

                if (result.IsFailure)
                {
                    return result.ToProblem(context);
                }

                var role = result.Value;

                var preconditionResult = EntityTagHttp.HandleIfNoneMatch(
                    context,
                    role.RowVersion);

                if (preconditionResult is not null)
                {
                    return preconditionResult;
                }

                return TypedResults.Ok(role);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Roles)
            .Produces<RoleResponse>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesStrongEntityTag(
                StatusCodes.Status200OK,
                StatusCodes.Status304NotModified)
            .SupportsIfNoneMatch()
            .RequirePermission(UserManagement.Domain.Constants.UserManagementPermissions.Roles.Read);
    }
}