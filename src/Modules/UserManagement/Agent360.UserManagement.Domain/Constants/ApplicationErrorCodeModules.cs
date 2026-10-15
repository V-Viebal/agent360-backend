namespace Agent360.UserManagement.Domain.Constants;

/// <summary>
/// Provides short, stable module identifiers used as the first segment of User Management error codes.
/// </summary>
public static class ApplicationErrorCodeModules
{
    /// <summary>
    /// Gets the User Management module identifier.
    /// Keep this as a short code rather than a display name so errors stay readable.
    /// </summary>
    public const string UserManagement = "UM";
}