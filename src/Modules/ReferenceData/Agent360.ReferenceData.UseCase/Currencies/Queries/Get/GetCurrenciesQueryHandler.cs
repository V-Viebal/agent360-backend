using Hmz.Core.SharedKernel.Messaging;
using Agent360.ReferenceData.UseCase.Abstractions;
using Agent360.ReferenceData.UseCase.Currencies.Common;

namespace Agent360.ReferenceData.UseCase.Currencies.Queries.Get;

public sealed class GetCurrenciesQueryHandler(IGetCurrenciesQueryService queryService)
    : IQueryHandler<GetCurrenciesQuery, IReadOnlyList<CurrencyResponse>>
{
    public ValueTask<IReadOnlyList<CurrencyResponse>> Handle(
        GetCurrenciesQuery request,
        CancellationToken cancellationToken) =>
        new(queryService.GetAsync(request.ActiveOnly, cancellationToken));
}