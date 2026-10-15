using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.ReferenceData.UseCase.Currencies.Common;
using Agent360.ReferenceData.UseCase.Currencies.Queries.Get;

namespace Agent360.API.Features.ReferenceData.Currencies.Get;

public sealed class GetCurrenciesEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder) =>
        routeBuilder
            .MapGet(
                "reference-data/currencies",
                async Task<IResult> (
                    bool activeOnly,
                    ISender sender,
                    CancellationToken cancellationToken) =>
                    TypedResults.Ok(
                        await sender.Send(
                            new GetCurrenciesQuery(activeOnly),
                            cancellationToken)))
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetCurrencies")
            .WithTags(EndpointTags.ReferenceData)
            .WithSummary("Get currencies")
            .WithDescription(
                "Returns currencies ordered by code. The activeOnly query parameter is required.")
            .Produces<IReadOnlyList<CurrencyResponse>>()
            .ProducesValidationProblem()
            .RequireAuthorization();
}