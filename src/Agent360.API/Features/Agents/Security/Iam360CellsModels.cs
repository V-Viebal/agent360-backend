using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Security;

public sealed record Iam360McpClientBindingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record Iam360BuiltinToolBindingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("async_execution")] bool AsyncExecution = false);

public sealed record Iam360SecurityPostureDto(
    [property: JsonPropertyName("approval_level")] string ApprovalLevel,
    [property: JsonPropertyName("tool_guard_enabled")] bool ToolGuardEnabled,
    [property: JsonPropertyName("denied_tools")] IReadOnlyList<string> DeniedTools,
    [property: JsonPropertyName("file_guard_enabled")] bool FileGuardEnabled,
    [property: JsonPropertyName("sandbox_enabled")] bool SandboxEnabled);

public sealed record Iam360CellMembershipDto(
    [property: JsonPropertyName("cell_id")] string CellId,
    [property: JsonPropertyName("organization")] string Organization,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("lifecycle")] string Lifecycle);

public sealed record Iam360PermissionTreeNodeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("subtitle")] string Subtitle,
    [property: JsonPropertyName("level")] string Level, // "identity" | "organization_cell" | "role_authority" | "capability_group" | "capability_leaf" | "guardrail"
    [property: JsonPropertyName("effect")] string Effect, // "ALLOW" | "DENY" | "SCOPED"
    [property: JsonPropertyName("badge")] string Badge,
    [property: JsonPropertyName("accent")] string Accent, // "peach" | "mint" | "sky" | "lavender" | "rose"
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("children")] IReadOnlyList<Iam360PermissionTreeNodeDto> Children);

public sealed record Iam360IdentityCellDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("subject_type")] string SubjectType,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("instance_key")] string InstanceKey,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("canonical_name")] string CanonicalName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("role_tag")] string RoleTag,
    [property: JsonPropertyName("cell_role")] string CellRole,
    [property: JsonPropertyName("is_root_agent")] bool IsRootAgent,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("organization_cell_id")] string OrganizationCellId,
    [property: JsonPropertyName("primary_org_cell")] string PrimaryOrgCell,
    [property: JsonPropertyName("owner_identity_cell_id")] string OwnerIdentityCellId,
    [property: JsonPropertyName("controller_subject_id")] string ControllerSubjectId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("assurance")] string Assurance,
    [property: JsonPropertyName("assurance_level")] string AssuranceLevel,
    [property: JsonPropertyName("attested")] bool Attested,
    [property: JsonPropertyName("attested_at")] string AttestedAt,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("observed_revision")] int ObservedRevision,
    [property: JsonPropertyName("drift")] bool Drift,
    [property: JsonPropertyName("revoked")] bool Revoked,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("runtime_version")] string RuntimeVersion,
    [property: JsonPropertyName("node")] string Node,
    [property: JsonPropertyName("cluster_node")] string ClusterNode,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("workspace_dir")] string WorkspaceDir,
    [property: JsonPropertyName("system_prompt_files")] IReadOnlyList<string> SystemPromptFiles,
    [property: JsonPropertyName("enabled_channels")] IReadOnlyList<string> EnabledChannels,
    [property: JsonPropertyName("mcp_clients")] IReadOnlyList<Iam360McpClientBindingDto> McpClients,
    [property: JsonPropertyName("mcp_count")] int McpCount,
    [property: JsonPropertyName("enabled_mcp_count")] int EnabledMcpCount,
    [property: JsonPropertyName("builtin_tools")] IReadOnlyList<Iam360BuiltinToolBindingDto> BuiltinTools,
    [property: JsonPropertyName("tool_count")] int ToolCount,
    [property: JsonPropertyName("enabled_tool_count")] int EnabledToolCount,
    [property: JsonPropertyName("security")] Iam360SecurityPostureDto Security,
    [property: JsonPropertyName("machine_identity_ref")] string MachineIdentityRef,
    [property: JsonPropertyName("memberships")] IReadOnlyList<Iam360CellMembershipDto> Memberships,
    [property: JsonPropertyName("permission_tree")] Iam360PermissionTreeNodeDto PermissionTree);

public sealed record Iam360OrgCellMemberDto(
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("added_by")] string AddedBy,
    [property: JsonPropertyName("added_at")] string AddedAt);

public sealed record Iam360RebacTupleDto(
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("object")] string Object);

public sealed record Iam360ContextBindingDto(
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("session_prefix")] string SessionPrefix);

public sealed record Iam360PolicyRuleDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("description")] string Description);

public sealed record Iam360OrganizationCellDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("cell_id")] string CellId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organization")] string Organization,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("cluster_id")] string ClusterId,
    [property: JsonPropertyName("cluster_node")] string ClusterNode,
    [property: JsonPropertyName("node_ip")] string NodeIp,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("owner_identity_cell_id")] string OwnerIdentityCellId,
    [property: JsonPropertyName("owner_slug")] string OwnerSlug,
    [property: JsonPropertyName("owner_name")] string OwnerName,
    [property: JsonPropertyName("lifecycle")] string Lifecycle,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("default_deny")] bool DefaultDeny,
    [property: JsonPropertyName("runtime_enforcement")] string RuntimeEnforcement,
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("desired_revision")] int DesiredRevision,
    [property: JsonPropertyName("observed_revision")] int ObservedRevision,
    [property: JsonPropertyName("members")] IReadOnlyDictionary<string, Iam360OrgCellMemberDto> Members,
    [property: JsonPropertyName("member_count")] int MemberCount,
    [property: JsonPropertyName("total_identities_count")] int TotalIdentitiesCount,
    [property: JsonPropertyName("active_agents_count")] int ActiveAgentsCount,
    [property: JsonPropertyName("context_bindings")] IReadOnlyList<Iam360ContextBindingDto> ContextBindings,
    [property: JsonPropertyName("rebac_tuples")] IReadOnlyList<Iam360RebacTupleDto> RebacTuples,
    [property: JsonPropertyName("baseline_rules")] IReadOnlyList<Iam360PolicyRuleDto> BaselineRules,
    [property: JsonPropertyName("organization_rules")] IReadOnlyList<Iam360PolicyRuleDto> OrganizationRules,
    [property: JsonPropertyName("baseline_policy_hash")] string BaselinePolicyHash,
    [property: JsonPropertyName("organization_policy_hash")] string OrganizationPolicyHash,
    [property: JsonPropertyName("org_key_id")] string OrgKeyId,
    [property: JsonPropertyName("last_read_back")] string LastReadBack,
    [property: JsonPropertyName("telemetry_source")] string TelemetrySource,
    [property: JsonPropertyName("permission_tree")] Iam360PermissionTreeNodeDto PermissionTree);

public sealed record Iam360CellsSummaryDto(
    [property: JsonPropertyName("total_agents")] int TotalAgents,
    [property: JsonPropertyName("active_agents")] int ActiveAgents,
    [property: JsonPropertyName("disabled_agents")] int DisabledAgents,
    [property: JsonPropertyName("identity_cells_count")] int IdentityCellsCount,
    [property: JsonPropertyName("organization_cells_count")] int OrganizationCellsCount,
    [property: JsonPropertyName("runtimes_count")] int RuntimesCount,
    [property: JsonPropertyName("total_mcp_bindings")] int TotalMcpBindings,
    [property: JsonPropertyName("total_tool_bindings")] int TotalToolBindings,
    [property: JsonPropertyName("total_rebac_tuples")] int TotalRebacTuples,
    [property: JsonPropertyName("signed_intents_count")] int SignedIntentsCount = 0);

public sealed record Iam360CellsStrategyResponseDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("checked_at")] string CheckedAt,
    [property: JsonPropertyName("summary")] Iam360CellsSummaryDto Summary,
    [property: JsonPropertyName("organization_cells")] IReadOnlyList<Iam360OrganizationCellDto> OrganizationCells,
    [property: JsonPropertyName("identity_cells")] IReadOnlyList<Iam360IdentityCellDto> IdentityCells,
    [property: JsonPropertyName("global_hierarchy_tree")] Iam360PermissionTreeNodeDto GlobalHierarchyTree,
    [property: JsonPropertyName("signed_intents_ledger")] IReadOnlyList<Iam360SignedIntentEnvelopeDto>? SignedIntentsLedger = null);

public sealed record Iam360AgentCellInspectionDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("checked_at")] string CheckedAt,
    [property: JsonPropertyName("identity_cell")] Iam360IdentityCellDto IdentityCell,
    [property: JsonPropertyName("primary_organization_cell")] Iam360OrganizationCellDto PrimaryOrganizationCell,
    [property: JsonPropertyName("member_organization_cells")] IReadOnlyList<Iam360OrganizationCellDto> MemberOrganizationCells,
    [property: JsonPropertyName("permission_tree")] Iam360PermissionTreeNodeDto PermissionTree);

public sealed record Iam360IdentityStatusMutationRequest(
    [property: JsonPropertyName("action")] string Action, // "activate" | "suspend" | "revoke"
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record Iam360OrgRoleMutationRequest(
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("role")] string Role, // "owner" | "manager" | "employee"
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record Iam360OrgEnforcementMutationRequest(
    [property: JsonPropertyName("runtime_enforcement")] string RuntimeEnforcement, // "off" | "preview" | "on"
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record Iam360EvaluateAccessRequest(
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("organization_cell_id")] string? OrganizationCellId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("resource_path")] string? ResourcePath = null);

public sealed record Iam360EvaluateAccessResponse(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("matched_rule")] string MatchedRule,
    [property: JsonPropertyName("role_in_cell")] string RoleInCell,
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("organization_cell_id")] string OrganizationCellId,
    [property: JsonPropertyName("trace_path")] IReadOnlyList<string> TracePath);

public sealed record Iam360DetachedSignatureDto(
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("public_key")] string PublicKey,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("value")] string Value);

public sealed record Iam360SignedIntentEnvelopeDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("intent_id")] string IntentId,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("cell_id")] string CellId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("issued_at")] string IssuedAt,
    [property: JsonPropertyName("expires_at")] string ExpiresAt,
    [property: JsonPropertyName("payload")] IReadOnlyDictionary<string, object?> Payload,
    [property: JsonPropertyName("signature")] Iam360DetachedSignatureDto Signature,
    [property: JsonPropertyName("upstream_sync_status")] string UpstreamSyncStatus,
    [property: JsonPropertyName("upstream_target")] string UpstreamTarget);

public sealed record Iam360LiveAgentTestItemDto(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("organization_cell_id")] string OrganizationCellId,
    [property: JsonPropertyName("role_in_cell")] string RoleInCell,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("cross_org_memberships_count")] int CrossOrgMembershipsCount,
    [property: JsonPropertyName("permission_tree_branches_count")] int PermissionTreeBranchesCount,
    [property: JsonPropertyName("mcp_count")] int McpCount,
    [property: JsonPropertyName("tool_count")] int ToolCount,
    [property: JsonPropertyName("workspace_read_allowed")] bool WorkspaceReadAllowed,
    [property: JsonPropertyName("baseline_secret_denied")] bool BaselineSecretDenied,
    [property: JsonPropertyName("passed")] bool Passed);

public sealed record Iam360FleetAuditReportDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("executed_at")] string ExecutedAt,
    [property: JsonPropertyName("total_agents_tested")] int TotalAgentsTested,
    [property: JsonPropertyName("passed_count")] int PassedCount,
    [property: JsonPropertyName("failed_count")] int FailedCount,
    [property: JsonPropertyName("organization_cells_tested")] int OrganizationCellsTested,
    [property: JsonPropertyName("signed_intents_verified")] int SignedIntentsVerified,
    [property: JsonPropertyName("results")] IReadOnlyList<Iam360LiveAgentTestItemDto> Results);
