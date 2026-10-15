namespace Agent360.UserManagement.Domain.UserAggregate.ValueObjects;

public static class PhoneNumberErrorMessages
{
    public const string InvalidCountryCode = "The phone country code '{0}' is invalid.";
    public const string CountryCodeMustStartWith = "The phone country code must start with '{0}'";
    public const string NumberRequired = "The phone number is required";
}