namespace Agent360.ReferenceData.UseCase.Currencies.Common;

public sealed record CurrencyResponse(
    long Id,
    string Code,
    string Symbol,
    int DecimalDigits,
    bool IsActive);