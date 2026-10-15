namespace Agent360.UserManagement.Domain.Constants;

public static class UserManagementPermissions
{
    public static class Roles
    {
        public const string Read = "USER-MANAGEMENT.ROLES.READ";
        public const string Manage = "USER-MANAGEMENT.ROLES.MANAGE";
    }

    public static class Permissions
    {
        public const string Read = "USER-MANAGEMENT.PERMISSIONS.READ";
    }

    public static class Users
    {
        public const string Read = "USER-MANAGEMENT.USERS.READ";
        public const string ManageRoles = "USER-MANAGEMENT.USERS.MANAGE-ROLES";
    }

    public static IReadOnlyList<(string Code, string Name, string Description)> Catalog { get; } =
    [
        (Roles.Read, "Read roles", "View roles and their metadata."),
        (Roles.Manage, "Manage roles", "Create, update, and delete roles and assignments."),
        (Permissions.Read, "Read permissions", "View the application permission catalog."),
        (Users.Read, "Read users", "View user profiles and assignments."),
        (Users.ManageRoles, "Manage user roles", "Replace role assignments for another user.")
    ];
}