namespace Agent360.UserManagement.UseCase.Abstractions;

public interface IUserIdentityService
{
    /// <summary>
    /// Resolves a local long ID from an external IDP string.
    /// </summary>
    Task<long?> ResolveLocalUserIdAsync(string externalUserId, CancellationToken ct = default);

    /// <summary>
    /// Called when user profile data changes — invalidates resolution cache.
    /// </summary>
    Task InvalidateAsync(string externalUserId, CancellationToken ct = default);
}