using Asp.Versioning;
using Hmz.Core.SharedKernel.Identity.Abstractions;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.UserManagement.UseCase.Users.Queries.GetCurrentProfile;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Npgsql;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agent360.API.Features.Users.GetCurrentProfile;

public sealed record UpdateMyProfileRequest(string? DisplayName, string? Email, string? AvatarUrl);

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

        routeBuilder.MapMethods(
            "users/me",
            ["PATCH", "PUT"],
            async Task<IResult> (
                UpdateMyProfileRequest req,
                ICurrentUser currentUser,
                IConfiguration config,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(currentUser.ExternalUserId) || currentUser.UserId is not { } userId || userId <= 0)
                {
                    return TypedResults.Unauthorized();
                }

                var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
                if (string.IsNullOrWhiteSpace(connStr))
                {
                    return Results.Problem("Database not configured");
                }

                var cleanName = req.DisplayName?.Trim();
                var cleanEmail = req.Email?.Trim();
                var cleanAvatar = req.AvatarUrl?.Trim();

                string? firstName = null;
                string? lastName = null;
                if (!string.IsNullOrWhiteSpace(cleanName))
                {
                    var parts = cleanName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    firstName = parts.Length > 0 ? parts[0] : cleanName;
                    lastName = parts.Length > 1 ? parts[1] : "";
                }

                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);

                await using var cmd = new NpgsqlCommand(@"
                    UPDATE user_management.""Users""
                    SET ""DisplayName"" = COALESCE(NULLIF($1, ''), ""DisplayName""),
                        ""FirstName"" = COALESCE(NULLIF($2, ''), ""FirstName""),
                        ""LastName"" = CASE WHEN $2 IS NOT NULL AND $2 <> '' THEN $3 ELSE ""LastName"" END,
                        ""Email"" = COALESCE(NULLIF($4, ''), ""Email""),
                        ""AvatarUrl"" = COALESCE(NULLIF($5, ''), ""AvatarUrl""),
                        ""ModifiedAt"" = NOW()
                    WHERE ""Id"" = $6;
                ", conn);

                cmd.Parameters.AddWithValue((object?)cleanName ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)firstName ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)lastName ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)cleanEmail ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)cleanAvatar ?? DBNull.Value);
                cmd.Parameters.AddWithValue(userId);
                await cmd.ExecuteNonQueryAsync(ct);

                return Results.Ok(new { success = true, userId });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("UpdateCurrentUserProfile")
            .WithTags(EndpointTags.Users)
            .WithSummary("Update the current authenticated user's profile (displayName, email, avatarUrl)")
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
