using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Models;

public sealed record QwenPawActiveModel(
    [property: JsonPropertyName("provider_id")] string? ProviderId,
    [property: JsonPropertyName("model")] string? Model);

public sealed record QwenPawAgentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("workspace_dir")] string? WorkspaceDir,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("pinned")] bool Pinned,
    [property: JsonPropertyName("startup_status")] string? StartupStatus,
    [property: JsonPropertyName("backend")] string? Backend,
    [property: JsonPropertyName("backend_capabilities")] Dictionary<string, object>? BackendCapabilities,
    [property: JsonPropertyName("backend_model")] string? BackendModel,
    [property: JsonPropertyName("backend_reasoning_effort")] string? BackendReasoningEffort,
    [property: JsonPropertyName("active_model")] QwenPawActiveModel? ActiveModel,
    [property: JsonPropertyName("managed_by_app")] string? ManagedByApp,
    [property: JsonPropertyName("available_in_chat")] bool AvailableInChat);

public sealed record QwenPawAgentListResponse(
    [property: JsonPropertyName("agents")] IReadOnlyList<QwenPawAgentDto> Agents);

public sealed record AgentSummaryResponse(
    string Id,
    string Name,
    string Slug,
    string? RoleTag,
    string? Node,
    string Status,
    string? Model,
    string RuntimeVersion,
    string? Uptime,
    string? Description,
    bool Enabled,
    bool Pinned,
    bool AvailableInChat,
    IReadOnlyList<AgentToolSummary> Tools,
    bool CanToggle = true,
    bool IsRootAgent = false,
    [property: JsonPropertyName("avatar_config")] JsonNode? AvatarConfig = null);

public sealed record AgentToolSummary(
    string Name,
    string Type,
    string Description);

public sealed record ToggleAgentRequest(
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record ToggleAgentResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record PinAgentRequest(
    [property: JsonPropertyName("pinned")] bool Pinned);

public sealed record PinAgentResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("pinned")] bool Pinned);
