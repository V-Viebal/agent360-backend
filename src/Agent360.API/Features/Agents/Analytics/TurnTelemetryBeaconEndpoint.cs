using System.Collections.Concurrent;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Analytics;

public sealed record TurnTelemetryRecord(
    string AgentId,
    string SessionId,
    string Model,
    double TtftMs,
    double TotalDurationMs,
    int TokenCount,
    double TokensPerSec,
    double EstimatedCostUsd,
    string Node,
    DateTime RecordedAt);

public static class TelemetryStore
{
    private static readonly ConcurrentQueue<TurnTelemetryRecord> _records = new();

    public static void Record(TurnTelemetryRecord record)
    {
        _records.Enqueue(record);
        while (_records.Count > 1000)
        {
            _records.TryDequeue(out _);
        }
    }

    public static IReadOnlyList<TurnTelemetryRecord> GetRecent(int count = 50) =>
        _records.Reverse().Take(count).ToList();
}

public sealed class TurnTelemetryBeaconEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost(
            "agents/analytics/turn-telemetry",
            ([FromBody] TurnTelemetryRecord payload) =>
            {
                var record = payload with { RecordedAt = DateTime.UtcNow };
                TelemetryStore.Record(record);
                return TypedResults.Ok(new { success = true, count = TelemetryStore.GetRecent(1).Count });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("RecordTurnTelemetry")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Ingest real-time turn inference speed and TTFT telemetry")
            .AllowAnonymous();

        routeBuilder.MapGet(
            "agents/analytics/turn-telemetry/recent",
            ([FromQuery] int count = 20) =>
            {
                var list = TelemetryStore.GetRecent(count);
                return TypedResults.Ok(list);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetRecentTurnTelemetry")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Retrieve recently recorded turn telemetries")
            .AllowAnonymous();
    }
}
