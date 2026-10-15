namespace Agent360.ReferenceData.UseCase.Languages.Common;

public sealed record LanguageResponse(
    long Id,
    string Name,
    string Code,
    bool IsActive);