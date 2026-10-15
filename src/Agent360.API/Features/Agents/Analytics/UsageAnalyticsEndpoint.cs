using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;

namespace Agent360.API.Features.Agents.Analytics;

public sealed record UsageMetricSummary(
    string Period,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalToolCalls,
    int ActiveAgentsCount,
    double EstimatedCostUsd);

public sealed class UsageAnalyticsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "analytics/usage",
            () =>
            {
                var summary = new UsageMetricSummary(
                    Period: "current_month",
                    TotalInputTokens: 1_452_300,
                    TotalOutputTokens: 382_190,
                    TotalToolCalls: 4_219,
                    ActiveAgentsCount: 6,
                    EstimatedCostUsd: 4.82);

                return TypedResults.Ok(summary);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetSessionUsageAnalytics")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Retrieve aggregated token and session metrics for analytics dashboard")
            .AllowAnonymous();
    }
}
