using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Filters;
using Agent360.API.Identity;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.UseCase.Abstractions;
using Agent360.UserManagement.UseCase.Users.Commands.CreateByLogtoWebhook;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;

namespace Agent360.API.Features.Users.Webhooks.Logto.SignUp;

public sealed record LogtoWebhookRequest(
    string Event,
    DateTimeOffset CreatedAt,
    string? InteractionEvent,
    string? UserAgent,
    string? ApplicationId,
    string? SessionId,
    string? Ip,
    LogtoApplication? Application,
    JsonElement Data,
    string HookId
);

public sealed record LogtoApplication(
    string Id,
    string Name,
    string Type,
    string? Description
);

public sealed record UserCreatedPayload(
    string Id,
    string? Username,
    string? PrimaryEmail,
    string? PrimaryPhone,
    string? Name,
    string? Avatar,
    bool IsSuspended,
    long CreatedAt,
    long UpdatedAt,
    long? LastSignInAt,
    string? ApplicationId,
    JsonElement Profile,
    JsonElement CustomData,
    JsonElement Identities
);

public class UserWebhookLogtoSignUpEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost("users/webhooks/logto/signup",
            async Task<Results<BadRequest, NoContent>> (
            HttpContext context,
            ISender sender,
            IUserIdentityService userIdentityService,
            CurrentUser currentUser,
            CancellationToken ct
            ) =>
            {
                LogtoWebhookRequest? request;
                UserCreatedPayload? payload;

                try
                {
                    request = await context.Request.ReadFromJsonAsync<LogtoWebhookRequest>(ct);
                    payload = request?.Data.Deserialize<UserCreatedPayload>(new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException)
                {
                    return TypedResults.BadRequest();
                }

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.Id) ||
                    string.IsNullOrWhiteSpace(payload.PrimaryEmail))
                {
                    return TypedResults.BadRequest();
                }

                var requestUserId = await ResolveRequestUserIdAsync(userIdentityService, ct);
                currentUser.SetResolvedUserId(requestUserId);

                // Handle signup - create user in local DB, send welcome email, etc.
                await sender.Send(new CreateByLogtoWebhookCommand(
                    payload.Id,
                    payload.PrimaryEmail,
                    payload.Name ?? string.Empty,
                    string.Empty,
                    payload.Avatar), ct);

                return TypedResults.NoContent();
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("SignUpUserByLogtoWebhook")
            .WithTags(EndpointTags.Users)
            .Produces(StatusCodes.Status204NoContent)
            // Add filter for signature validation
            .AllowAnonymous()
            .AddEndpointFilter<LogtoWebhookSignatureFilter>()
            .AddOpenApiOperationTransformer((op, context, ct) =>
            {
                // Remove all configured security schemes.
                op.Security = [];
                return Task.CompletedTask;
            });
    }

    private static async Task<long> ResolveRequestUserIdAsync(
        IUserIdentityService userIdentityService,
        CancellationToken cancellationToken)
    {
        var userId = await userIdentityService.ResolveLocalUserIdAsync(
            SystemUsers.ExternalId,
            cancellationToken);

        userId ??= await userIdentityService.ResolveLocalUserIdAsync(
            SystemUsers.SystemId,
            cancellationToken);

        return userId
            ?? throw new InvalidOperationException(
                "Neither the Logto external account nor the system account has been seeded.");
    }
}