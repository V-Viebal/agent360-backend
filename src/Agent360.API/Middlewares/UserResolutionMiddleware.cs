using Hmz.Core.SharedKernel.Authorization;
using Agent360.API.Identity;
using Agent360.UserManagement.Domain.UserAggregate;
using Agent360.UserManagement.Infrastructure.Authorization;
using Agent360.UserManagement.Infrastructure.Persistence;
using Agent360.UserManagement.UseCase.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agent360.API.Middlewares;

public sealed class UserResolutionMiddleware(RequestDelegate next, ILogger<UserResolutionMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IUserIdentityService userIdentityService,
        CurrentUser currentUser,
        PermissionSnapshotResolver permissionResolver,
        IPermissionSnapshotAccessor snapshotAccessor,
        IServiceScopeFactory scopeFactory)
    {
        if (currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
        {
            var localId = await userIdentityService.ResolveLocalUserIdAsync(currentUser.ExternalUserId, context.RequestAborted);
            
            // JIT auto-provision user if they authenticate via Logto but do not exist in local DB yet
            if (localId is null)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    
                    var existing = await db.Users.FirstOrDefaultAsync(u => u.LogtoUserId == currentUser.ExternalUserId, context.RequestAborted);
                    if (existing is null)
                    {
                        var email = context.User.FindFirst("email")?.Value ?? $"{currentUser.ExternalUserId}@skyer.hmz.one";
                        var name = context.User.FindFirst("name")?.Value ?? "User";
                        var parts = name.Split(' ', 2);
                        var firstName = parts[0];
                        var lastName = parts.Length > 1 ? parts[1] : "Skyer";
                        
                        // Default to Customer role (Id 1) or Admin role (Id 2) if admin claim present
                        var isAdmin = context.User.FindAll("roles").Concat(context.User.FindAll(System.Security.Claims.ClaimTypes.Role))
                            .Any(r => string.Equals(r.Value, "admin", StringComparison.OrdinalIgnoreCase));
                        var defaultRoleId = isAdmin ? 2L : 1L;

                        var newUser = User.Register(
                            currentUser.ExternalUserId,
                            email,
                            defaultRoleId,
                            firstName,
                            lastName
                        );

                        db.Users.Add(newUser);
                        await db.SaveChangesAsync(context.RequestAborted);
                        localId = newUser.Id;
                        logger.LogInformation("Auto-provisioned local user {UserId} for Logto sub {ExternalUserId}", localId, currentUser.ExternalUserId);
                    }
                    else
                    {
                        localId = existing.Id;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to auto-provision user for external ID '{ExternalUserId}'", currentUser.ExternalUserId);
                }
            }

            currentUser.SetResolvedUserId(localId);

            if (localId is { } userId)
            {
                try
                {
                    snapshotAccessor.Set(await permissionResolver.ResolveAsync(userId, context.RequestAborted));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not resolve permission snapshot for user {UserId}", userId);
                }
            }
        }

        await next(context);
    }
}
