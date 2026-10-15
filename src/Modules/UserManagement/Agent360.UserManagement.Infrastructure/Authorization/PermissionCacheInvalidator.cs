using Agent360.UserManagement.Infrastructure.Persistence;
using Agent360.UserManagement.UseCase.Permissions.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Agent360.UserManagement.Infrastructure.Authorization;

public sealed class PermissionCacheInvalidator(AppDbContext db, HybridCache cache) : IPermissionCacheInvalidator
{
    /// <summary>
    /// Invalidates permission snapshots for every user assigned to the
    /// specified role. Each user snapshot is cached under a stable
    /// <c>um:permissions:user:{userId}</c> tag, so this method queries
    /// the role's current user assignments and evicts each one.
    /// </summary>
    public async Task InvalidateRoleAsync(
        long roleId,
        CancellationToken cancellationToken = default)
    {
        var userIds = await db.Set<Domain.UserAggregate.Entities.UserRole>()
            .AsNoTracking()
            .Where(ur => ur.RoleId == roleId)
            .Select(ur => ur.UserId)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        await InvalidateUsersAsync(userIds, cancellationToken);
    }

    /// <summary>
    /// Invalidates the permission snapshot for a single user, for example
    /// when the user's role assignments are modified.
    /// </summary>
    public async Task InvalidateUserAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        await cache.RemoveByTagAsync(
            $"um:permissions:user:{userId}",
            cancellationToken);
    }

    /// <summary>
    /// Invalidates permission snapshots for multiple users simultaneously.
    /// </summary>
    public async Task InvalidateUsersAsync(
        IEnumerable<long> userIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var userId in userIds)
        {
            await cache.RemoveByTagAsync(
                $"um:permissions:user:{userId}",
                cancellationToken);
        }
    }
}