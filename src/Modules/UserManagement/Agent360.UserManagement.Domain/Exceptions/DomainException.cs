namespace Agent360.UserManagement.Domain.Exceptions;

public abstract class DomainException : Exception
{
    public string Code { get; set; }

    protected DomainException(string code, string message) : base(message)
    {
        Code = code;
    }
}