using Hmz.Core.SharedKernel.Messaging;
using Agent360.ReferenceData.UseCase.Languages.Common;

namespace Agent360.ReferenceData.UseCase.Languages.Queries.Get;

public sealed record GetLanguagesQuery(bool ActiveOnly)
    : IQuery<IReadOnlyList<LanguageResponse>>;