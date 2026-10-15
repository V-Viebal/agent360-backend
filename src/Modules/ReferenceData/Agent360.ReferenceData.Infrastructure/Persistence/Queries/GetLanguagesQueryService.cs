using Agent360.ReferenceData.UseCase.Abstractions;
using Agent360.ReferenceData.UseCase.Languages.Common;
using Microsoft.EntityFrameworkCore;

namespace Agent360.ReferenceData.Infrastructure.Persistence.Queries;

public sealed class GetLanguagesQueryService(AppDbContext dbContext)
    : IGetLanguagesQueryService
{
    public async Task<IReadOnlyList<LanguageResponse>> GetAsync(
        bool activeOnly,
        CancellationToken cancellationToken = default) =>
        await dbContext.Languages
            .AsNoTracking()
            .Where(item => !activeOnly || item.IsActive)
            .OrderBy(item => item.Name)
            .Select(item => new LanguageResponse(
                item.Id,
                item.Name,
                item.Code,
                item.IsActive))
            .ToListAsync(cancellationToken);
}