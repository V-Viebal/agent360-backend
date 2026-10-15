using Hmz.Core.SharedKernel.Results;
using Agent360.UserManagement.Domain.Constants;

namespace Agent360.UserManagement.Domain.UserAggregate.ValueObjects;

public static class PhoneNumberErrorCodes
{
    public static readonly string InvalidCountryCode = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "user.phone-number.invalid-country-code");
    public static readonly string CountryCodeMustStartWith = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "user.phone-number.country-code-must-start-with");
    public static readonly string NumberRequired = ErrorCode.Create(ApplicationErrorCodeModules.UserManagement, ErrorType.Validation, "user.phone-number.number-required");
}