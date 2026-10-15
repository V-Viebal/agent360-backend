using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.RoleAggregate;

namespace Agent360.UserManagement.UseCase.Roles.Common;

public static class RoleErrors
{
    public static readonly Error NotFound = Error.NotFound(
        RoleApplicationErrorCodes.NotFound,
        "Role was not found.");

    public static readonly Error DuplicateCode = Error.Conflict(
        RoleApplicationErrorCodes.CodeAlreadyExists,
        "A role with the same code already exists.");

    public static readonly Error SystemRoleImmutable = Error.Conflict(
        RoleErrorCodes.SystemRoleImmutable,
        "System roles cannot be updated or deleted.");

    public static readonly Error InvalidPermissionIds = Error.Validation(
        RoleApplicationErrorCodes.InvalidPermissionIds,
        "One or more permission IDs are invalid.");

    public static readonly Error AdminOnlyPermission = Error.Validation(
        RoleApplicationErrorCodes.AdminOnlyPermission,
        "The selected permission can be assigned only to the ADMIN role.",
        "permissionIds");

    public static readonly Error RequiredAdminPermission = Error.Validation(
        RoleApplicationErrorCodes.RequiredAdminPermission,
        "The ADMIN role must retain the user-role management permission.",
        "permissionIds");

    public static Error Invalid(string code, string message) =>
        Error.Validation(code, message, GetTarget(code));

    private static string? GetTarget(string code)
    {
        if (code == RoleErrorCodes.NameRequired ||
            code == RoleErrorCodes.NameTooLong)
        {
            return "name";
        }

        if (code == RoleErrorCodes.CodeRequired ||
            code == RoleErrorCodes.CodeTooLong)
        {
            return "code";
        }

        return null;
    }
}