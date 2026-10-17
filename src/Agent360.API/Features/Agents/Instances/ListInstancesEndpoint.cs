using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Instances;

public sealed class ListInstancesEndpoint : IEndpoint
{
    public const string RouteName = "ListInfraInstances";

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. GET /api/v1/instances - List all cluster runtime node instances across infra
        routeBuilder.MapGet(
            "instances",
            async Task<IResult> (
                IQwenPawClient client,
                CancellationToken cancellationToken) =>
            {
                var agents = await client.GetAgentsAsync(cancellationToken);

                // Group dynamic agents by cluster node/instance
                var coreAgents = agents.Where(a => a.Node?.Contains("3rd") == true || a.Node?.Contains("Core") == true).Select(a => a.Slug).ToList();
                var gmAgents = agents.Where(a => a.Node?.Contains("6th") == true || a.Node?.Contains("Gia Mộc") == true).Select(a => a.Slug).ToList();
                var climaxAgents = agents.Where(a => a.Node?.Contains("Climax") == true || a.Slug.StartsWith("climax-")).Select(a => a.Slug).ToList();
                var planthyAgents = agents.Where(a => a.Node?.Contains("Planthy") == true || a.Slug.StartsWith("planthy-")).Select(a => a.Slug).ToList();
                var swarmAgents = agents.Where(a => a.Node?.Contains("AgentTeams") == true || a.Slug.StartsWith("agentteams-")).Select(a => a.Slug).ToList();
                var llmAgents = agents.Where(a => a.Node?.Contains("LLM360") == true || a.Slug.Contains("llm360")).Select(a => a.Slug).ToList();

                var instances = new List<InfraNodeInstanceResponse>
                {
                    new(
                        InstanceId: "inst:node-3rd:core",
                        NodeName: "3rd",
                        NodeIp: "100.76.185.111",
                        Role: "Agent Core Hub & Reasoning Runtime (QwenPaw Master)",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "qwenpaw",
                        EndpointUrl: "http://100.76.185.111:33148",
                        ActiveAgentsCount: coreAgents.Count,
                        TotalAgentsCount: coreAgents.Count,
                        Agents: coreAgents,
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy"),

                    new(
                        InstanceId: "inst:node-6th:gm",
                        NodeName: "6th",
                        NodeIp: "100.81.66.42",
                        Role: "Gia Mộc Dedicated Operations & Workflow Fleet",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "qwenpaw-isolated",
                        EndpointUrl: "http://100.81.66.42:26888",
                        ActiveAgentsCount: gmAgents.Count,
                        TotalAgentsCount: gmAgents.Count,
                        Agents: gmAgents,
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy"),

                    new(
                        InstanceId: "inst:node-5th:climax",
                        NodeName: "5th",
                        NodeIp: "100.70.201.83",
                        Role: "Climax Creative Autonomous Agent Fleet",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "qwenpaw-isolated",
                        EndpointUrl: "http://100.70.201.83:26404",
                        ActiveAgentsCount: climaxAgents.Count,
                        TotalAgentsCount: climaxAgents.Count,
                        Agents: climaxAgents,
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy"),

                    new(
                        InstanceId: "inst:node-5th:planthy",
                        NodeName: "5th",
                        NodeIp: "100.70.201.83",
                        Role: "Planthy AI Delivery Office & Project Management",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "qwenpaw-isolated",
                        EndpointUrl: "http://100.70.201.83:32841",
                        ActiveAgentsCount: planthyAgents.Count,
                        TotalAgentsCount: planthyAgents.Count,
                        Agents: planthyAgents,
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy"),

                    new(
                        InstanceId: "inst:node-4th:agentteams",
                        NodeName: "4th",
                        NodeIp: "100.77.205.126",
                        Role: "AgentTeams Swarm Orchestrator & Matrix AI Gateway",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "agentteams",
                        EndpointUrl: "https://manager-ai.hmz.one",
                        ActiveAgentsCount: swarmAgents.Count,
                        TotalAgentsCount: swarmAgents.Count,
                        Agents: swarmAgents,
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy"),

                    new(
                        InstanceId: "inst:node-4th:llm360",
                        NodeName: "4th",
                        NodeIp: "100.77.205.126",
                        Role: "LLM360 Unified Inference Provider Fleet",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "llm360",
                        EndpointUrl: "https://proxy-llm360.hmz.one",
                        ActiveAgentsCount: llmAgents.Count,
                        TotalAgentsCount: llmAgents.Count,
                        Agents: llmAgents,
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy"),

                    new(
                        InstanceId: "inst:node-7th:control",
                        NodeName: "7th",
                        NodeIp: "100.99.201.47",
                        Role: "Infrastructure Control Plane & Fleet Guardian",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "infra-guardian",
                        EndpointUrl: "https://control.hmz.one",
                        ActiveAgentsCount: 1,
                        TotalAgentsCount: 1,
                        Agents: new List<string> { "fleet-sre-guardian" },
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy"),

                    new(
                        InstanceId: "inst:node-2nd:agent360",
                        NodeName: "2nd",
                        NodeIp: "100.109.210.72",
                        Role: "Agent360 Platform Host & Workspace Edge",
                        Status: "ready",
                        ClusterId: "antares-vps-cluster",
                        RuntimeType: "agent360-fullstack",
                        EndpointUrl: "https://api-agent360.antares.vn",
                        ActiveAgentsCount: agents.Count,
                        TotalAgentsCount: agents.Count,
                        Agents: agents.Select(a => a.Slug).ToList(),
                        LastHeartbeat: DateTimeOffset.UtcNow.ToString("O"),
                        Health: "healthy")
                };

                return TypedResults.Ok(instances);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName(RouteName)
            .WithTags(EndpointTags.Agents)
            .WithSummary("List all infra cluster instances")
            .WithDescription("Retrieves the fleet of compute nodes and runtime host instances powering Agent360 autonomous agents.")
            .Produces<IReadOnlyList<InfraNodeInstanceResponse>>()
            .AllowAnonymous();

        // 2. GET /api/v1/bots/{botId}/deployments & instances
        routeBuilder.MapGet(
            "bots/{botId}/deployments",
            async Task<IResult> (
                string botId,
                IQwenPawClient client,
                CancellationToken cancellationToken) =>
            {
                var cleanSlug = botId.Replace("bot:agent360:", "").ToLowerInvariant().Trim();
                var agents = await client.GetAgentsAsync(cancellationToken);
                var matched = agents.FirstOrDefault(a => a.Slug.Equals(cleanSlug, StringComparison.OrdinalIgnoreCase));

                if (matched is null)
                {
                    return Results.NotFound(new { message = $"Bot '{botId}' has no deployment on cluster" });
                }

                var (targetNode, endpointUrl) = ResolveBotPlacement(matched.Slug, matched.Node);

                var deployment = new BotDeploymentInstanceResponse(
                    DeploymentId: $"bot-deployment:{matched.Slug}:production",
                    BotId: matched.Id,
                    BotSlug: matched.Slug,
                    Environment: "production",
                    ClusterId: "antares-vps-cluster",
                    RuntimeType: matched.RuntimeVersion.Contains("agentteams") ? "agentteams" : "qwenpaw",
                    RuntimeId: $"runtime-{matched.Slug}-prod",
                    TargetNode: targetNode,
                    EndpointUrl: endpointUrl,
                    KernelVersion: "agent360-infra-kernel v2.2.1",
                    Status: matched.Status,
                    IsActive: matched.Enabled,
                    UpdatedAt: DateTimeOffset.UtcNow.ToString("O"));

                return TypedResults.Ok(new[] { deployment });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetBotDeployments")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Get active runtime deployment instances for a specific bot")
            .Produces<IReadOnlyList<BotDeploymentInstanceResponse>>()
            .AllowAnonymous();
    }

    private static (string TargetNode, string EndpointUrl) ResolveBotPlacement(string slug, string? node)
    {
        var s = slug.ToLowerInvariant();
        if (s.StartsWith("gm-")) return ("Node 6th (Gia Mộc)", "https://ai-gm.antares.vn");
        if (s.StartsWith("climax-")) return ("Node 5th (Climax)", "https://ai-climax.antares.vn");
        if (s.StartsWith("planthy-")) return ("Node 5th (Planthy)", "https://planthy-ai.hmz.one");
        if (s.StartsWith("agentteams-")) return ("Node 4th (AgentTeams)", "https://manager-ai.hmz.one");
        if (s.Contains("llm360")) return ("Node 4th (LLM360)", "https://proxy-llm360.hmz.one");
        if (s == "tsx-copaw") return ("Node 4th (TSX)", "https://ai-tsx.hmz.one");

        return ("Node 3rd (Core)", "https://ai.hmz.one");
    }
}
