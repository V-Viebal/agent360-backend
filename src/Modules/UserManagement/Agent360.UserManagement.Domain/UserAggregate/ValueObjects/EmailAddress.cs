using Ardalis.SharedKernel;
using Agent360.UserManagement.Domain.UserAggregate.Exceptions;

namespace Agent360.UserManagement.Domain.UserAggregate.ValueObjects;

public sealed class EmailAddress : ValueObject
{
    public string Value { get; }

    private EmailAddress()
    {
        Value = string.Empty;
    }

    private EmailAddress(string value)
    {
        Value = value;
    }

    public static EmailAddress Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidEmailException(value);

        value = value.Trim().ToLowerInvariant();

        if (!value.Contains("@"))
            throw new InvalidEmailException(value);

        return new EmailAddress(value);
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}