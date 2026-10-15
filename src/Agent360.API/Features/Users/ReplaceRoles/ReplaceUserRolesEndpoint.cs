using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.UseCase.Users.Commands.ReplaceRoles;
using Agent360.UserManagement.UseCase.Users.Common;

namespace Agent360.API.Features.Users.ReplaceRoles;

public sealed class ReplaceUserRolesEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPut(
                "users/{id:long}/roles",
                async Task<IResult> (
                    long id,
                    ReplaceUserRolesRequest request,
                    HttpContext context,
                    ISender sender,
                    CancellationToken cancellationToken) =>
                {
                    var precondition = EntityTagHttp.HandleIfMatch(
                        context,
                        out var expectedRowVersion);

                    if (precondition is not null)
                    {
                        return precondition;
                    }

                    var result = await sender.Send(
                        new ReplaceUserRolesCommand(
                            id,
                            request.RoleIds,
                            expectedRowVersion),
                        cancellationToken);

                    if (result.IsFailure)
                    {
                        return result.ToProblem(context);
                    }

                    EntityTagHttp.WriteStrong(
                        context.Response,
                        result.Value.RowVersion);
                    return TypedResults.Ok(result.Value);
                })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ReplaceUserRoles")
            .WithTags(EndpointTags.Users)
            .WithSummary("Replace another user's role assignments")
            .Produces<UserResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequiresStrongIfMatch()
            .ProducesStrongEntityTag(StatusCodes.Status200OK)
            .RequirePermission(UserManagementPermissions.Users.ManageRoles);
    }
}

public sealed class ReplaceUserRolesRequest
{
    public required IReadOnlyList<long> RoleIds { get; init; }
}