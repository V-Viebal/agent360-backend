namespace Agent360.UserManagement.Domain.RoleAggregate;

public static class RoleErrorMessages
{
    public const string NameRequired = "Role name is required.";
    public const string NameTooLong = "Role name cannot exceed {0} characters.";
    public const string CodeRequired = "Role code is required.";
    public const string CodeTooLong = "Role code cannot exceed {0} characters.";
    public const string SystemRoleImmutable = "System roles cannot be updated or deleted.";
}