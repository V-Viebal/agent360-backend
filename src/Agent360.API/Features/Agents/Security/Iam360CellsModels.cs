using System.Text.Json.Serialization;

namespace Agent360.API.Features.Agents.Security;

public sealed record Iam360McpClientBindingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("drift")] bool Drift = false);

public sealed record Iam360BuiltinToolBindingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("async_execution")] bool AsyncExecution = false,
    [property: JsonPropertyName("drift")] bool Drift = false);

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

public sealed record Iam360ObservedPrincipalDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("topic_title")] string? TopicTitle,
    [property: JsonPropertyName("agent_slug")] string AgentSlug,
    [property: JsonPropertyName("organization_cell_id")] string OrganizationCellId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("verified_at")] string VerifiedAt);

public sealed record Iam360A2ADelegationTargetDto(
    [property: JsonPropertyName("target_slug")] string TargetSlug,
    [property: JsonPropertyName("target_name")] string TargetName,
    [property: JsonPropertyName("target_identity_cell_id")] string TargetIdentityCellId,
    [property: JsonPropertyName("organization_cell_id")] string OrganizationCellId,
    [property: JsonPropertyName("caller_role")] string CallerRole,
    [property: JsonPropertyName("target_role")] string TargetRole,
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record Iam360PermissionTreeNodeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("subtitle")] string Subtitle,
    [property: JsonPropertyName("level")] string Level, // "identity" | "organization_cell" | "role_authority" | "capability_group" | "capability_leaf" | "guardrail" | "principal_leaf" | "a2a_leaf"
    [property: JsonPropertyName("effect")] string Effect, // "ALLOW" | "DENY" | "SCOPED" | "STEP_UP_REQUIRED"
    [property: JsonPropertyName("badge")] string Badge,
    [property: JsonPropertyName("accent")] string Accent, // "peach" | "mint" | "sky" | "lavender" | "rose"
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("children")] IReadOnlyList<Iam360PermissionTreeNodeDto> Children,
    [property: JsonPropertyName("capability_kind")] string? CapabilityKind = null, // "mcp" | "tool" | null
    [property: JsonPropertyName("capability_key")] string? CapabilityKey = null,
    [property: JsonPropertyName("can_toggle")] bool CanToggle = false,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("drift")] bool Drift = false);

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
    [property: JsonPropertyName("observed_principals")] IReadOnlyList<Iam360ObservedPrincipalDto> ObservedPrincipals,
    [property: JsonPropertyName("a2a_delegation_targets")] IReadOnlyList<Iam360A2ADelegationTargetDto> A2ADelegationTargets,
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

public sealed record Iam360TwoPersonApprovalDto(
    [property: JsonPropertyName("approval_id")] string ApprovalId,
    [property: JsonPropertyName("kind")] string Kind, // "owner_transfer" | "step_up_action" | "revoke_identity" | "retire_org"
    [property: JsonPropertyName("cell_id")] string CellId,
    [property: JsonPropertyName("target_identity_cell_id")] string TargetIdentityCellId,
    [property: JsonPropertyName("previous_owner_identity_cell_id")] string? PreviousOwnerIdentityCellId,
    [property: JsonPropertyName("new_owner_identity_cell_id")] string? NewOwnerIdentityCellId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("resource_path")] string? ResourcePath,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("status")] string Status, // "pending" | "approved" | "rejected"
    [property: JsonPropertyName("requested_by")] string RequestedBy,
    [property: JsonPropertyName("requested_at")] string RequestedAt,
    [property: JsonPropertyName("decided_by")] string? DecidedBy = null,
    [property: JsonPropertyName("decided_at")] string? DecidedAt = null,
    [property: JsonPropertyName("decision_reason")] string? DecisionReason = null);

public sealed record Iam360ReconciliationStatusDto(
    [property: JsonPropertyName("status")] string Status, // "in_sync" | "drift_detected" | "reconciling"
    [property: JsonPropertyName("last_reconciled_at")] string LastReconciledAt,
    [property: JsonPropertyName("interval_seconds")] int IntervalSeconds,
    [property: JsonPropertyName("total_agents_checked")] int TotalAgentsChecked,
    [property: JsonPropertyName("passed_agents")] int PassedAgents,
    [property: JsonPropertyName("drift_count")] int DriftCount,
    [property: JsonPropertyName("pending_approvals_count")] int PendingApprovalsCount,
    [property: JsonPropertyName("signed_intents_count")] int SignedIntentsCount);

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
    [property: JsonPropertyName("signed_intents_count")] int SignedIntentsCount = 0,
    [property: JsonPropertyName("observed_principals_count")] int ObservedPrincipalsCount = 0,
    [property: JsonPropertyName("pending_approvals_count")] int PendingApprovalsCount = 0,
    [property: JsonPropertyName("drift_count")] int DriftCount = 0);

public sealed record Iam360CellsStrategyResponseDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("checked_at")] string CheckedAt,
    [property: JsonPropertyName("summary")] Iam360CellsSummaryDto Summary,
    [property: JsonPropertyName("reconciliation")] Iam360ReconciliationStatusDto Reconciliation,
    [property: JsonPropertyName("organization_cells")] IReadOnlyList<Iam360OrganizationCellDto> OrganizationCells,
    [property: JsonPropertyName("identity_cells")] IReadOnlyList<Iam360IdentityCellDto> IdentityCells,
    [property: JsonPropertyName("approvals")] IReadOnlyList<Iam360TwoPersonApprovalDto> Approvals,
    [property: JsonPropertyName("global_hierarchy_tree")] Iam360PermissionTreeNodeDto GlobalHierarchyTree,
    [property: JsonPropertyName("signed_intents_ledger")] IReadOnlyList<Iam360SignedIntentEnvelopeDto>? SignedIntentsLedger = null);

public sealed record Iam360AgentCellInspectionDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("checked_at")] string CheckedAt,
    [property: JsonPropertyName("reconciliation")] Iam360ReconciliationStatusDto Reconciliation,
    [property: JsonPropertyName("identity_cell")] Iam360IdentityCellDto IdentityCell,
    [property: JsonPropertyName("primary_organization_cell")] Iam360OrganizationCellDto PrimaryOrganizationCell,
    [property: JsonPropertyName("member_organization_cells")] IReadOnlyList<Iam360OrganizationCellDto> MemberOrganizationCells,
    [property: JsonPropertyName("pending_approvals")] IReadOnlyList<Iam360TwoPersonApprovalDto> PendingApprovals,
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

public sealed record Iam360CapabilityToggleRequest(
    [property: JsonPropertyName("capability_kind")] string CapabilityKind, // "mcp" | "tool"
    [property: JsonPropertyName("capability_key")] string CapabilityKey,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record Iam360OwnerTransferRequest(
    [property: JsonPropertyName("new_owner_identity_cell_id")] string NewOwnerIdentityCellId,
    [property: JsonPropertyName("requested_by")] string? RequestedBy = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record Iam360ApprovalDecisionRequest(
    [property: JsonPropertyName("approve")] bool Approve,
    [property: JsonPropertyName("decided_by")] string? DecidedBy = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record Iam360EvaluateAccessRequest(
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("organization_cell_id")] string? OrganizationCellId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("resource_path")] string? ResourcePath = null,
    [property: JsonPropertyName("target_agent_slug")] string? TargetAgentSlug = null);

public sealed record Iam360EvaluateAccessResponse(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("effect")] string Effect, // "EFFECT_ALLOW" | "EFFECT_DENY" | "STEP_UP_APPROVAL_REQUIRED"
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("matched_rule")] string MatchedRule,
    [property: JsonPropertyName("role_in_cell")] string RoleInCell,
    [property: JsonPropertyName("identity_cell_id")] string IdentityCellId,
    [property: JsonPropertyName("organization_cell_id")] string OrganizationCellId,
    [property: JsonPropertyName("trace_path")] IReadOnlyList<string> TracePath,
    [property: JsonPropertyName("step_up_approval")] Iam360TwoPersonApprovalDto? StepUpApproval = null);

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
    [property: JsonPropertyName("observed_principals_count")] int ObservedPrincipalsCount,
    [property: JsonPropertyName("a2a_allowed_targets_count")] int A2AAllowedTargetsCount,
    [property: JsonPropertyName("workspace_read_allowed")] bool WorkspaceReadAllowed,
    [property: JsonPropertyName("baseline_secret_denied")] bool BaselineSecretDenied,
    [property: JsonPropertyName("a2a_cross_cell_denied")] bool A2ACrossCellDenied,
    [property: JsonPropertyName("passed")] bool Passed);

public sealed record Iam360FleetAuditReportDto(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("executed_at")] string ExecutedAt,
    [property: JsonPropertyName("total_agents_tested")] int TotalAgentsTested,
    [property: JsonPropertyName("passed_count")] int PassedCount,
    [property: JsonPropertyName("failed_count")] int FailedCount,
    [property: JsonPropertyName("organization_cells_tested")] int OrganizationCellsTested,
    [property: JsonPropertyName("signed_intents_verified")] int SignedIntentsVerified,
    [property: JsonPropertyName("pending_approvals_count")] int PendingApprovalsCount,
    [property: JsonPropertyName("results")] IReadOnlyList<Iam360LiveAgentTestItemDto> Results);
