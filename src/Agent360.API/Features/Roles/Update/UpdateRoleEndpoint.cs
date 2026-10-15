using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.UseCase.Roles.Commands.Update;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.API.Features.Roles.Update;

public sealed class UpdateRoleEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPut(
            "roles/{id:long}",
            async Task<Microsoft.AspNetCore.Http.IResult> (
                long id,
                UpdateRoleRequest request,
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
                    new UpdateRoleCommand(
                        id,
                        request.Name,
                        request.Code,
                        expectedRowVersion),
                    cancellationToken);

                if (result.IsFailure)
                {
                    return result.ToProblem(context);
                }

                var role = result.Value;
                EntityTagHttp.WriteStrong(context.Response, role.RowVersion);

                return TypedResults.Ok(role);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("UpdateRole")
            .WithTags(EndpointTags.Roles)
            .Produces<RoleResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequiresStrongIfMatch()
            .ProducesStrongEntityTag(StatusCodes.Status200OK)
            .RequirePermission(Agent360.UserManagement.Domain.Constants.UserManagementPermissions.Roles.Manage);
    }
}