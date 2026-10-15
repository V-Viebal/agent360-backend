using Hmz.Core.SharedKernel.Messaging;
using Agent360.ReferenceData.UseCase.Abstractions;
using Agent360.ReferenceData.UseCase.Languages.Common;

namespace Agent360.ReferenceData.UseCase.Languages.Queries.Get;

public sealed class GetLanguagesQueryHandler(IGetLanguagesQueryService queryService)
    : IQueryHandler<GetLanguagesQuery, IReadOnlyList<LanguageResponse>>
{
    public ValueTask<IReadOnlyList<LanguageResponse>> Handle(
        GetLanguagesQuery request,
        CancellationToken cancellationToken) =>
        new(queryService.GetAsync(request.ActiveOnly, cancellationToken));
}