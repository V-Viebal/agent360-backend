using Agent360.ReferenceData.UseCase.Abstractions;
using Agent360.ReferenceData.UseCase.Currencies.Common;
using Microsoft.EntityFrameworkCore;

namespace Agent360.ReferenceData.Infrastructure.Persistence.Queries;

public sealed class GetCurrenciesQueryService(AppDbContext dbContext)
    : IGetCurrenciesQueryService
{
    public async Task<IReadOnlyList<CurrencyResponse>> GetAsync(
        bool activeOnly,
        CancellationToken cancellationToken = default) =>
        await dbContext.Currencies
            .AsNoTracking()
            .Where(item => !activeOnly || item.IsActive)
            .OrderBy(item => item.Code)
            .Select(item => new CurrencyResponse(
                item.Id,
                item.Code,
                item.Symbol,
                item.DecimalDigits,
                item.IsActive))
            .ToListAsync(cancellationToken);
}