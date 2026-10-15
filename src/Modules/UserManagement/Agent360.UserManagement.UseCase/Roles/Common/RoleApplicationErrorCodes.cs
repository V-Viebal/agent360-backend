using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.Constants;

namespace Agent360.UserManagement.UseCase.Roles.Common;

public static class RoleApplicationErrorCodes
{
    // NOTF already communicates "not found"; keep the final segment semantic and concise.
    public static readonly string NotFound = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.NotFound, "role");
    public static readonly string CodeAlreadyExists = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Conflict, "role.code-already-exists");
    public static readonly string InvalidSearch = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.search-invalid");
    public static readonly string InvalidPermissionIds = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.invalid-permission-ids");
    public static readonly string AdminOnlyPermission = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.admin-only-permission");
    public static readonly string RequiredAdminPermission = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.required-admin-permission");
}