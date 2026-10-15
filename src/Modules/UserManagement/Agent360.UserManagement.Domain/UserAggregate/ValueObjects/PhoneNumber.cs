using Ardalis.SharedKernel;
using Agent360.UserManagement.Domain.UserAggregate.Exceptions;

namespace Agent360.UserManagement.Domain.UserAggregate.ValueObjects;

public sealed class PhoneNumber : ValueObject
{
    public string CountryCode { get; }
    public string Number { get; }
    public string? Extension { get; }

    private PhoneNumber()
    {
        CountryCode = string.Empty;
        Number = string.Empty;
    }

    private PhoneNumber(string countryCode, string number, string? extension)
    {
        CountryCode = countryCode;
        Number = number;
        Extension = extension;
    }

    public static PhoneNumber Create(string countryCode, string number, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            throw new InvalidPhoneNumberException(
                PhoneNumberErrorCodes.InvalidCountryCode,
                string.Format(PhoneNumberErrorMessages.InvalidCountryCode, countryCode));

        if (!countryCode.StartsWith("+"))
            throw new InvalidPhoneNumberException(
                PhoneNumberErrorCodes.CountryCodeMustStartWith,
                string.Format(PhoneNumberErrorMessages.CountryCodeMustStartWith, "+"));

        if (string.IsNullOrWhiteSpace(number))
            throw new InvalidPhoneNumberException(
                PhoneNumberErrorCodes.NumberRequired,
                PhoneNumberErrorMessages.NumberRequired);

        // normalize
        number = Normalize(number);

        return new PhoneNumber(countryCode, number, extension);
    }

    private static string Normalize(string number)
    {
        return new string(number.Where(char.IsDigit).ToArray());
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return CountryCode;
        yield return Number;
        yield return Extension ?? string.Empty;
    }

    public override string ToString()
    {
        return $"{CountryCode}{Number}" +
               (string.IsNullOrWhiteSpace(Extension) ? "" : $" x{Extension}");
    }
}