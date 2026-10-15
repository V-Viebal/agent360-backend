using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.Constants;

namespace Agent360.UserManagement.Domain.RoleAggregate;

public static class RoleErrorCodes
{
    public static readonly string NameRequired = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.name-required");
    public static readonly string NameTooLong = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.name-too-long");
    public static readonly string CodeRequired = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.code-required");
    public static readonly string CodeTooLong = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "role.code-too-long");
    public static readonly string SystemRoleImmutable = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Conflict, "role.system-role-immutable");
}