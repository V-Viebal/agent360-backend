using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.ReferenceData.UseCase.Languages.Common;
using Agent360.ReferenceData.UseCase.Languages.Queries.Get;

namespace Agent360.API.Features.ReferenceData.Languages.Get;

public sealed class GetLanguagesEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder) =>
        routeBuilder
            .MapGet(
                "reference-data/languages",
                async Task<IResult> (
                    bool activeOnly,
                    ISender sender,
                    CancellationToken cancellationToken) =>
                    TypedResults.Ok(
                        await sender.Send(
                            new GetLanguagesQuery(activeOnly),
                            cancellationToken)))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetLanguages")
            .WithTags(EndpointTags.ReferenceData)
            .WithSummary("Get languages")
            .WithDescription(
                "Returns languages ordered by name. The activeOnly query parameter is required.")
            .Produces<IReadOnlyList<LanguageResponse>>()
            .ProducesValidationProblem()
            .RequireAuthorization();
}