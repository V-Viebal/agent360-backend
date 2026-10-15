using Asp.Versioning;
using Hmz.Core.SharedKernel.Results;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.UseCase.Roles.Commands.Delete;
using Microsoft.EntityFrameworkCore;

namespace Agent360.API.Features.Roles.Delete;

public sealed class DeleteRoleEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapDelete(
            "roles/{id:long}",
            async Task<Microsoft.AspNetCore.Http.IResult> (
                long id,
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

                Result result;

                try
                {
                    result = await sender.Send(
                        new DeleteRoleCommand(id, expectedRowVersion),
                        cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return PreconditionResults.ConcurrentWrite(context);
                }

                if (result.IsFailure)
                {
                    return result.ToProblem(context);
                }

                return TypedResults.NoContent();
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("DeleteRole")
            .WithTags(EndpointTags.Roles)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequiresStrongIfMatch()
            .RequirePermission(Agent360.UserManagement.Domain.Constants.UserManagementPermissions.Roles.Manage);
    }
}