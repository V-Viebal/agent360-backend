using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.Constants;

namespace Agent360.UserManagement.Domain.UserAggregate;

public static class UserErrorCodes
{
    public static readonly string InvalidEmail = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "user.invalid-email");
    public static readonly string LogtoUserIdRequired = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "user.logto-user-id-required");
}