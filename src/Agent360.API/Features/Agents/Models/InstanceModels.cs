using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record InfraNodeInstanceResponse(
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("node_name")] string NodeName,
    [property: JsonPropertyName("node_ip")] string NodeIp,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("cluster_id")] string ClusterId,
    [property: JsonPropertyName("runtime_type")] string RuntimeType,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("active_agents_count")] int ActiveAgentsCount,
    [property: JsonPropertyName("total_agents_count")] int TotalAgentsCount,
    [property: JsonPropertyName("agents")] IReadOnlyList<string> Agents,
    [property: JsonPropertyName("last_heartbeat")] string LastHeartbeat,
    [property: JsonPropertyName("health")] string Health);

public sealed record BotDeploymentInstanceResponse(
    [property: JsonPropertyName("deployment_id")] string DeploymentId,
    [property: JsonPropertyName("bot_id")] string BotId,
    [property: JsonPropertyName("bot_slug")] string BotSlug,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("cluster_id")] string ClusterId,
    [property: JsonPropertyName("runtime_type")] string RuntimeType,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("target_node")] string TargetNode,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("kernel_version")] string KernelVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);
