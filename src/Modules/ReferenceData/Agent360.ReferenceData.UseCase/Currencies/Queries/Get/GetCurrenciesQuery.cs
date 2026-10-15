using Hmz.Core.SharedKernel.Messaging;
using Agent360.ReferenceData.UseCase.Currencies.Common;

namespace Agent360.ReferenceData.UseCase.Currencies.Queries.Get;

public sealed record GetCurrenciesQuery(bool ActiveOnly)
    : IQuery<IReadOnlyList<CurrencyResponse>>;