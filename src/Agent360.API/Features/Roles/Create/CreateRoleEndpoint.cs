using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Roles.GetById;
using Agent360.UserManagement.UseCase.Roles.Commands.Create;
using Agent360.UserManagement.UseCase.Roles.Common;

namespace Agent360.API.Features.Roles.Create;

public sealed class CreateRoleEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost(
            "roles",
            async Task<IResult> (
                CreateRoleRequest request,
                HttpContext context,
                ISender sender,
                CancellationToken cancellationToken) =>
            {
                var noneMatchResult = EntityTagHttp.TryReadIfNoneMatchStar(
                    context.Request,
                    out var requireAbsent);

                if (noneMatchResult == EntityTagParseResult.Invalid)
                {
                    return PreconditionResults.InvalidIfNoneMatch(context);
                }

                var result = await sender.Send(
                    new CreateRoleCommand(
                        request.Name,
                        request.Code,
                        requireAbsent),
                    cancellationToken);

                if (result.IsFailure)
                {
                    return result.ToProblem(context);
                }

                var role = result.Value;
                EntityTagHttp.WriteStrong(context.Response, role.RowVersion);

                return TypedResults.CreatedAtRoute(
                    role,
                    GetRoleByIdEndpoint.RouteName,
                    new { version = "1", id = role.Id });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("CreateRole")
            .WithTags(EndpointTags.Roles)
            .Produces<RoleResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .SupportsIfNoneMatch(starOnly: true)
            .ProducesStrongEntityTag(StatusCodes.Status201Created)
            .RequirePermission(Agent360.UserManagement.Domain.Constants.UserManagementPermissions.Roles.Manage);
    }
}