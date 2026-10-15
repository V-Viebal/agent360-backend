using Agent360.UserManagement.Infrastructure.Persistence;
using Agent360.UserManagement.UseCase.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Agent360.UserManagement.Infrastructure.Identity;

public sealed class UserIdentityService(IServiceScopeFactory scopeFactory, HybridCache cache) : IUserIdentityService
{
    private const string CacheKeyPrefix = "user_local_id";
    private const string CacheTagPrefix = "user_local_id";

    public async Task InvalidateAsync(string externalUserId, CancellationToken ct = default)
        => await cache.RemoveByTagAsync(GetCacheTag(externalUserId), ct);

    public async Task<long?> ResolveLocalUserIdAsync(string externalUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return null;
        }

        return await cache.GetOrCreateAsync(
            key: $"{CacheKeyPrefix}:{externalUserId}",
            factory: async (innerCt) => await FetchAsync(externalUserId, innerCt),
            options: new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(30), // L2 (Redis) TTL,
                LocalCacheExpiration = TimeSpan.FromMinutes(5) // L1 (In-Memory) TTL
            },
            tags: GetCacheTag(externalUserId), // enables targeted cache invalidation,
            cancellationToken: ct
           );
    }

    private async Task<long?> FetchAsync(string externalUserId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = await dbContext.Users
            .Where(u => u.LogtoUserId == externalUserId)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(ct);

        return id == 0L ? null : id;
    }

    private string[] GetCacheTag(string externalUserId) => [$"{CacheTagPrefix}:{externalUserId}"];
}