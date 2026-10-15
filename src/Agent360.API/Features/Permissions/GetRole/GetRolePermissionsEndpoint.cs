using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.UseCase.Permissions.Common;
using Agent360.UserManagement.UseCase.Permissions.Queries.GetRole;

namespace Agent360.API.Features.Permissions.GetRole;

public sealed class GetRolePermissionsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
                "roles/{id:long}/permissions",
                async Task<IResult> (
                    long id,
                    HttpContext context,
                    ISender sender,
                    CancellationToken cancellationToken) =>
                {
                    var result = await sender.Send(
                        new GetRolePermissionsQuery(id),
                        cancellationToken);

                    if (result is null)
                    {
                        return TypedResults.NotFound();
                    }

                    var preconditionResult = EntityTagHttp.HandleIfNoneMatch(
                        context,
                        result.RowVersion);

                    return preconditionResult ?? TypedResults.Ok(result);
                })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetRolePermissions")
            .WithTags(EndpointTags.Permissions)
            .Produces<RolePermissionsResponse>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesStrongEntityTag(
                StatusCodes.Status200OK,
                StatusCodes.Status304NotModified)
            .SupportsIfNoneMatch()
            .RequirePermission(UserManagementPermissions.Permissions.Read);
    }

}