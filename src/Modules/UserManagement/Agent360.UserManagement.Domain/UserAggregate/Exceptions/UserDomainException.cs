using Agent360.UserManagement.Domain.Exceptions;

namespace Agent360.UserManagement.Domain.UserAggregate.Exceptions;

public class UserDomainException : DomainException
{
    public UserDomainException(string code, string message) : base(code, message)
    {
    }
}