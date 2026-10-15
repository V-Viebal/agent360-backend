using Agent360.ReferenceData.UseCase.Languages.Common;

namespace Agent360.ReferenceData.UseCase.Abstractions;

public interface IGetLanguagesQueryService
{
    Task<IReadOnlyList<LanguageResponse>> GetAsync(
        bool activeOnly,
        CancellationToken cancellationToken = default);
}