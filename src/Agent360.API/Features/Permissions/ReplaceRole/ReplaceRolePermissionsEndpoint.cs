using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.UseCase.Permissions.Commands.ReplaceRole;
using Agent360.UserManagement.UseCase.Permissions.Common;

namespace Agent360.API.Features.Permissions.ReplaceRole;

public sealed class ReplaceRolePermissionsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPut(
                "roles/{id:long}/permissions",
                async Task<IResult> (
                    long id,
                    ReplaceRolePermissionsRequest request,
                    HttpContext context,
                    ISender sender,
                    CancellationToken cancellationToken) =>
                {
                    var precondition = EntityTagHttp.HandleIfMatch(
                        context,
                        out var expectedRowVersion);

                    if (precondition is not null)
                        return precondition;

                    var result = await sender.Send(
                        new ReplaceRolePermissionsCommand(
                            id,
                            request.PermissionIds,
                            expectedRowVersion),
                        cancellationToken);

                    if (result.IsFailure)
                        return result.ToProblem(context);

                    EntityTagHttp.WriteStrong(context.Response, result.Value.RowVersion);
                    return TypedResults.Ok(result.Value);
                })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ReplaceRolePermissions")
            .WithTags(EndpointTags.Permissions)
            .Produces<RolePermissionsResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequiresStrongIfMatch()
            .ProducesStrongEntityTag(StatusCodes.Status200OK)
            .RequirePermission(UserManagementPermissions.Roles.Manage);
    }
}