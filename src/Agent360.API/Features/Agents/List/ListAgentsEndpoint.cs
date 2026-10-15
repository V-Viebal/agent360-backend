using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;

namespace Agent360.API.Features.Agents.List;

public sealed class ListAgentsEndpoint : IEndpoint
{
    public const string RouteName = "ListAgents";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(
            "agents",
            async Task<IResult> (
                IQwenPawClient client,
                CancellationToken cancellationToken) =>
            {
                var agents = await client.GetAgentsAsync(cancellationToken);
                return TypedResults.Ok(agents);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all infra agents")
            .WithDescription("Retrieves the list of active agents running on infrastructure mapped from the QwenPaw runtime adapter.")
            .Produces<IReadOnlyList<AgentSummaryResponse>>()
            .AllowAnonymous();
    }
}
