using Asp.Versioning;
using Hmz.Core.SharedKernel.Identity.Abstractions;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.UseCase.Users.Queries.GetCurrentProfile;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agent360.API.Features.Users.GetCurrentProfile;

public sealed class GetCurrentProfileEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "users/me",
            async Task<IResult> (
                HttpContext context,
                ICurrentUser currentUser,
                ISender sender,
                IOptions<JsonOptions> jsonOptions,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
                {
                    return TypedResults.Unauthorized();
                }

                if (currentUser.UserId is not { } userId)
                {
                    return TypedResults.NotFound();
                }

                var profile = await sender.Send(
                    new GetCurrentUserProfileQuery(userId),
                    cancellationToken);

                if (profile is null)
                {
                    return TypedResults.NotFound();
                }

                var entityTag = CreateEntityTag(
                    profile,
                    jsonOptions.Value.SerializerOptions);

                context.Response.Headers.CacheControl = "private, no-cache";
                context.Response.Headers.AppendCommaSeparatedValues(
                    HeaderNames.Vary,
                    HeaderNames.Authorization);

                var preconditionResult = EntityTagHttp.HandleIfNoneMatch(
                    context,
                    entityTag);

                if (preconditionResult is not null)
                {
                    return preconditionResult;
                }

                return TypedResults.Ok(profile);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetCurrentUserProfile")
            .WithTags(EndpointTags.Users)
            .WithSummary("Get the current user's profile and authorization state")
            .WithDescription(
                "Returns a strong ETag derived from the complete JSON representation, including " +
                "profile data, assigned roles, and effective permissions. Supports If-None-Match.")
            .Produces<CurrentUserProfileResponse>()
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesStrongEntityTag(
                StatusCodes.Status200OK,
                StatusCodes.Status304NotModified)
            .SupportsIfNoneMatch()
            .RequireAuthorization();
    }

    private static string CreateEntityTag(
        CurrentUserProfileResponse profile,
        JsonSerializerOptions serializerOptions)
    {
        var representation = JsonSerializer.SerializeToUtf8Bytes(
            profile,
            serializerOptions);

        var digest = Convert.ToHexString(SHA256.HashData(representation));

        return $"\"user-profile-{digest}\"";
    }
}