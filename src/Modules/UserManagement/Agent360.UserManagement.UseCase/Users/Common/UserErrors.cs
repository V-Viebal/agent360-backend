using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.Constants;

namespace Agent360.UserManagement.UseCase.Users.Common;

public static class UserApplicationErrorCodes
{
    public static readonly string NotFound = ErrorCode.Create(
        ApplicationErrorCodeModules.UserManagement,
        ErrorType.NotFound,
        "user");

    public static readonly string CannotAssignOwnRoles = ErrorCode.Create(
        ApplicationErrorCodeModules.UserManagement,
        ErrorType.Conflict,
        "user.cannot-assign-own-roles");

    public static readonly string InvalidRoleIds = ErrorCode.Create(
        ApplicationErrorCodeModules.UserManagement,
        ErrorType.Validation,
        "user.invalid-role-ids");

    public static readonly string SystemRoleAssignmentForbidden = ErrorCode.Create(
        ApplicationErrorCodeModules.UserManagement,
        ErrorType.Validation,
        "user.system-role-assignment-forbidden");

    public static readonly string InvalidSearch = ErrorCode.Create(
        ApplicationErrorCodeModules.UserManagement,
        ErrorType.Validation,
        "user.search-invalid");
}

public static class UserErrors
{
    public static readonly Error NotFound = Error.NotFound(
        UserApplicationErrorCodes.NotFound,
        "User was not found.");

    public static readonly Error CannotAssignOwnRoles = Error.Conflict(
        UserApplicationErrorCodes.CannotAssignOwnRoles,
        "Administrators cannot change their own role assignments.");

    public static readonly Error InvalidRoleIds = Error.Validation(
        UserApplicationErrorCodes.InvalidRoleIds,
        "One or more role IDs are invalid.",
        "roleIds");

    public static readonly Error SystemRoleAssignmentForbidden = Error.Validation(
        UserApplicationErrorCodes.SystemRoleAssignmentForbidden,
        "System roles cannot be assigned through the user administration API.",
        "roleIds");

}