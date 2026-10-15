using Hmz.Core.SharedKernel.Authorization;
using Agent360.API.Identity;
using Agent360.UserManagement.Infrastructure.Authorization;
using Agent360.UserManagement.UseCase.Abstractions;

namespace Agent360.API.Middlewares;

public sealed class UserResolutionMiddleware(RequestDelegate next, ILogger<UserResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IUserIdentityService userIdentityService, CurrentUser currentUser, PermissionSnapshotResolver permissionResolver, IPermissionSnapshotAccessor snapshotAccessor)
    {
        if (currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
        {
            var localId = await userIdentityService.ResolveLocalUserIdAsync(currentUser.ExternalUserId, context.RequestAborted);
            if (localId is null)
                logger.LogWarning("User with external ID '{ExternalUserId}' could not be resolved to a local user ID.", currentUser.ExternalUserId);

            currentUser.SetResolvedUserId(localId);

            if (localId is { } userId)
                snapshotAccessor.Set(await permissionResolver.ResolveAsync(userId, context.RequestAborted));
        }

        await next(context);
    }
}