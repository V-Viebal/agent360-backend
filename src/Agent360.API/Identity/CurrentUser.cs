using Hmz.Core.SharedKernel.Identity.Abstractions;
using System.Security.Claims;

namespace Agent360.API.Identity;

public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private long? _resolvedUserId;

    private ClaimsPrincipal? User => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public long? UserId => _resolvedUserId;

    public string? ExternalUserId => User?.FindFirst("sub")?.Value ?? string.Empty;

    internal void SetResolvedUserId(long? userId)
    {
        _resolvedUserId = userId;
    }
}