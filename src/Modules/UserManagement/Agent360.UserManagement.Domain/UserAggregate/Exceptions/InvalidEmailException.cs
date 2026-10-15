using Agent360.UserManagement.Domain.UserAggregate;

namespace Agent360.UserManagement.Domain.UserAggregate.Exceptions;

public class InvalidEmailException(string value)
    : UserDomainException(
        UserErrorCodes.InvalidEmail,
        string.Format(UserErrorMessages.InvalidEmail, value));