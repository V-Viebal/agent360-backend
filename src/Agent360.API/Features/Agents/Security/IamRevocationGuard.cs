using System.Collections.Concurrent;

namespace Agent360.API.Features.Agents.Security;

public static class IamRevocationGuard
{
    // Local memory registry for revoked Identity Cells
    private static readonly ConcurrentDictionary<string, string> RevokedIdentityCells = new(StringComparer.OrdinalIgnoreCase);

    public static void MarkIdentityCellRevoked(string identityCellId, string reason)
    {
        RevokedIdentityCells[identityCellId] = reason;
    }

    public static void MarkIdentityCellActive(string identityCellId)
    {
        RevokedIdentityCells.TryRemove(identityCellId, out _);
    }

    public static bool IsExecutionAllowed(string botId, out string? reason)
    {
        // botId format: bot:agent360:<slug> or <slug>
        var slug = botId.Replace("bot:agent360:", "").Trim();

        // Check if any matching identity cell is revoked
        foreach (var entry in RevokedIdentityCells)
        {
            if (entry.Key.EndsWith(slug, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"IAM360 Identity Cell '{entry.Key}' is revoked/suspended: {entry.Value}";
                return false;
            }
        }

        reason = null;
        return true;
    }
}
