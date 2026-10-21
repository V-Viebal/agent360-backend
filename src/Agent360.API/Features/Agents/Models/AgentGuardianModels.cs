using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record AgentGuardianClientInfoDto(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("transport")] string? Transport,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("toolCount")] int? ToolCount,
    [property: JsonPropertyName("toolNames")] IReadOnlyList<string>? ToolNames);

public sealed record AgentGuardianMcpDto(
    [property: JsonPropertyName("checkedAt")] string? CheckedAt,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("toolCount")] int? ToolCount,
    [property: JsonPropertyName("clientCount")] int? ClientCount,
    [property: JsonPropertyName("readyClientCount")] int? ReadyClientCount,
    [property: JsonPropertyName("clients")] IReadOnlyList<AgentGuardianClientInfoDto>? Clients);

public sealed record AgentGuardianModelValidationDto(
    [property: JsonPropertyName("ok")] bool? Ok,
    [property: JsonPropertyName("providerId")] string? ProviderId,
    [property: JsonPropertyName("model")] string? Model);

public sealed record AgentGuardianAgentDto(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("runtime")] string? Runtime,
    [property: JsonPropertyName("node")] string? Node,
    [property: JsonPropertyName("instance")] string? Instance,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl,
    [property: JsonPropertyName("cdpUrl")] string? CdpUrl,
    [property: JsonPropertyName("profileId")] string? ProfileId,
    [property: JsonPropertyName("gatusKey")] string? GatusKey,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("latencyMs")] int? LatencyMs,
    [property: JsonPropertyName("checkedAt")] string? CheckedAt,
    [property: JsonPropertyName("modelValidation")] AgentGuardianModelValidationDto? ModelValidation,
    [property: JsonPropertyName("stages")] Dictionary<string, string>? Stages,
    [property: JsonPropertyName("mcp")] AgentGuardianMcpDto? Mcp);

public sealed record AgentGuardianListResponse(
    [property: JsonPropertyName("agents")] IReadOnlyList<AgentGuardianAgentDto> Agents);
