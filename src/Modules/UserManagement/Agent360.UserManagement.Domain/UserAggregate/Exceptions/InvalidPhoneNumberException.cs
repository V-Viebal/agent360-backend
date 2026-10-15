namespace Agent360.UserManagement.Domain.UserAggregate.Exceptions;

public class InvalidPhoneNumberException(string code, string message) : UserDomainException(code, message);