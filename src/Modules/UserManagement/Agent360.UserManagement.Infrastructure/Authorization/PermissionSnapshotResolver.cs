using Hmz.Core.SharedKernel.Authorization;
using Agent360.UserManagement.Domain.RoleAggregate.Entities;
using Agent360.UserManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Agent360.UserManagement.Infrastructure.Authorization;

public sealed class PermissionSnapshotAccessor : IPermissionSnapshotAccessor
{
    public PermissionSnapshot Snapshot { get; private set; } = PermissionSnapshot.Empty();
    public void Set(PermissionSnapshot snapshot) => Snapshot = snapshot;
}

public sealed class PermissionSnapshotResolver(AppDbContext db, HybridCache cache)
{
    /// <summary>
    /// Resolves the permission snapshot for a user.
    /// Uses a stable cache key so the DB is only hit on a cache miss or
    /// after tag-based invalidation evicts the entry.
    /// </summary>
    public async Task<PermissionSnapshot> ResolveAsync(long userId, CancellationToken ct = default)
    {
        var key = $"um:permissions:user:{userId}";

        return await cache.GetOrCreateAsync<PermissionSnapshot>(
            key,
            async (innerCt) => await FetchAndBuildAsync(userId, innerCt),
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(30),
                LocalCacheExpiration = TimeSpan.FromMinutes(5)
            },
            tags: [$"um:permissions:user:{userId}"],
            cancellationToken: ct);
    }

    private async Task<PermissionSnapshot> FetchAndBuildAsync(long userId, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .Include(x => x.Roles)
            .SingleOrDefaultAsync(x => x.Id == userId, ct);

        if (user is null)
            return PermissionSnapshot.Empty(userId);

        var roleIds = user.Roles.Select(x => x.RoleId).Distinct().ToArray();

        if (roleIds.Length == 0)
            return PermissionSnapshot.Empty(userId);

        var activeRoles = await db.Roles
            .AsNoTracking()
            .Where(x => roleIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PermissionStamp })
            .ToListAsync(ct);

        if (activeRoles.Count == 0)
            return PermissionSnapshot.Empty(userId);

        var roleStamps = activeRoles.ToDictionary(x => x.Id, x => x.PermissionStamp);
        var activeRoleIds = roleStamps.Keys.ToArray();

        // Combine all distinct permissions across every active role assigned to this user.
        var permissions = await db
            .Set<RolePermission>()
            .AsNoTracking()
            .Where(x => activeRoleIds.Contains(x.RoleId))
            .Select(x => x.Permission.Code)
            .Distinct()
            .ToArrayAsync(ct);

        return new PermissionSnapshot(
            userId,
            permissions.ToHashSet(StringComparer.Ordinal),
            user.PermissionStamp,
            roleStamps);
    }
}