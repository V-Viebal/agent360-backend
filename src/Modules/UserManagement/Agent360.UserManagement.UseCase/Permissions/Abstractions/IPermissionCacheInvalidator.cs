namespace Agent360.UserManagement.UseCase.Permissions.Abstractions;

public interface IPermissionCacheInvalidator
{
    Task InvalidateRoleAsync(long roleId, CancellationToken cancellationToken = default);
    Task InvalidateUserAsync(long userId, CancellationToken cancellationToken = default);
    Task InvalidateUsersAsync(IEnumerable<long> userIds, CancellationToken cancellationToken = default);
}