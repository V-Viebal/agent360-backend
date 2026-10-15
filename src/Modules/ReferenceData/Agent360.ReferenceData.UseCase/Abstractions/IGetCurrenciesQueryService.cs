using Agent360.ReferenceData.UseCase.Currencies.Common;

namespace Agent360.ReferenceData.UseCase.Abstractions;

public interface IGetCurrenciesQueryService
{
    Task<IReadOnlyList<CurrencyResponse>> GetAsync(
        bool activeOnly,
        CancellationToken cancellationToken = default);
}