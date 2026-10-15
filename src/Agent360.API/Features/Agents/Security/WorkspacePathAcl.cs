namespace Agent360.API.Features.Agents.Security;

public static class WorkspacePathAcl
{
    private static readonly HashSet<string> DeniedExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        ".env.local",
        ".env.production",
        ".env.staging",
        "id_rsa",
        "id_ed25519",
        "credentials.json",
        "history.db"
    };

    private static readonly string[] DeniedPathSegments =
    [
        "/.git/",
        "/.agent/mcps/",
        "/.tmp/",
        "/working.secret/",
        "/ssh/",
        "/credentials/"
    ];

    public static bool IsPathAllowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();

        // Prevent path traversal
        if (normalized.Contains(".."))
        {
            return false;
        }

        // Check file name
        var fileName = Path.GetFileName(normalized);
        if (DeniedExactNames.Contains(fileName))
        {
            return false;
        }

        // Check path segments
        var padded = "/" + normalized.Trim('/') + "/";
        foreach (var segment in DeniedPathSegments)
        {
            if (padded.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
