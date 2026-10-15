using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.UseCase.Permissions.Common;
using Agent360.UserManagement.UseCase.Permissions.Queries.List;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agent360.API.Features.Permissions.List;

public sealed class ListPermissionsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
                "permissions",
                async Task<IResult> (
                    HttpContext context,
                    ISender sender,
                    CancellationToken cancellationToken) =>
                {
                    var result = await sender.Send(
                        new ListPermissionsQuery(),
                        cancellationToken);

                    var preconditionResult = EntityTagHttp.HandleIfNoneMatch(
                        context,
                        result.ETag);

                    return preconditionResult
                        ?? TypedResults.Ok(result.Permissions);
                })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListPermissions")
            .WithTags(EndpointTags.Permissions)
            .Produces<IReadOnlyList<PermissionResponse>>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesStrongEntityTag(
                StatusCodes.Status200OK,
                StatusCodes.Status304NotModified)
            .SupportsIfNoneMatch()
            .RequirePermission(UserManagementPermissions.Permissions.Read);
    }
}