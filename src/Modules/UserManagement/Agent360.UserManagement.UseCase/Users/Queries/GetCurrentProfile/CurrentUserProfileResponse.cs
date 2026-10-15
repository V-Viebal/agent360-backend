namespace Agent360.UserManagement.UseCase.Users.Queries.GetCurrentProfile;

public sealed record CurrentUserProfileResponse(
    long Id,
    string ExternalUserId,
    string? Email,
    CurrentUserPhoneNumberResponse? PhoneNumber,
    string FirstName,
    string LastName,
    string? DisplayName,
    string? AvatarUrl,
    long? PreferredCurrencyId,
    string? PreferredLanguageCode,
    string Status,
    uint RowVersion,
    IReadOnlyList<CurrentUserRoleResponse> Roles,
    IReadOnlyList<string> Permissions);

public sealed record CurrentUserPhoneNumberResponse(
    string CountryCode,
    string Number,
    string? Extension);

public sealed record CurrentUserRoleResponse(
    long Id,
    string Code,
    string Name);