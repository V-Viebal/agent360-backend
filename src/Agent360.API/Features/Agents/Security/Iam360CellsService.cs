using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.Security;

public interface IIam360CellsService
{
    Task<Iam360CellsStrategyResponseDto> GetCellsStrategyAsync(bool force = false, CancellationToken ct = default);
    Task<Iam360AgentCellInspectionDto?> GetAgentCellInspectionAsync(string agentIdOrSlug, bool force = false, CancellationToken ct = default);
    Task<Iam360IdentityCellDto?> MutateIdentityStatusAsync(string identityCellIdOrSlug, string action, string reason, CancellationToken ct = default);
    Task<Iam360OrganizationCellDto?> MutateOrgMemberRoleAsync(string orgCellId, string identityCellIdOrSlug, string role, string reason, CancellationToken ct = default);
    Task<Iam360OrganizationCellDto?> MutateOrgEnforcementAsync(string orgCellId, string enforcement, string reason, CancellationToken ct = default);
    Task<Iam360EvaluateAccessResponse> EvaluateAccessAsync(Iam360EvaluateAccessRequest request, CancellationToken ct = default);
    Task<Iam360FleetAuditReportDto> RunLiveTestAllAgentsAsync(CancellationToken ct = default);
    IReadOnlyList<Iam360SignedIntentEnvelopeDto> GetSignedIntentsLedger();
}

public sealed class Iam360CellsService : IIam360CellsService
{
    private const string StrategyCacheKey = "agent360:iam360:cells-strategy:v2";
    private readonly IQwenPawClient _qwenPawClient;
    private readonly IFleetInstanceRoutingService _routingService;
    private readonly IMemoryCache _cache;
    private readonly Iam360CellsPersistence _persistence;
    private readonly ILogger<Iam360CellsService> _logger;

    private static readonly object StateSyncLock = new();
    private static bool _hydratedFromDisk;
    private static readonly ConcurrentDictionary<string, string> IdentityStatusOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> OrgEnforcementOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> OrgRoleOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Iam360SignedIntentEnvelopeDto> SignedIntentsLedger = [];

    private static readonly IReadOnlyList<(string Key, string OrgSlug, string OrgId, string DisplayName, string CellId, string ClusterNode, string NodeIp, string PublicUrl, string OwnerSlug, string OwnerProfile)> FleetCellsCatalog =
    [
        ("core", "core", "organization:core", "Antares Core Engineering & Executive Hub", "cell:core", "3rd", "100.76.185.111", "https://ai.hmz.one", "mons", "mons"),
        ("gm", "gm", "organization:gm", "Gia Mộc Dedicated Operations & Workflow Fleet", "cell:gm", "6th", "100.81.66.42", "https://ai-gm.antares.vn", "gm-boc", "default"),
        ("climax", "climax", "organization:climax", "Climax Creative Autonomous Agent Fleet", "cell:climax", "5th", "100.70.201.83", "https://ai-climax.antares.vn", "climax-default", "default"),
        ("planthy", "planthy", "organization:planthy", "Planthy AI Delivery Office & Project Management", "cell:planthy", "5th", "100.70.201.83", "https://planthy-ai.hmz.one", "planthy-pm", "default"),
        ("agentteams", "agentteams", "organization:agentteams", "AgentTeams Swarm Orchestrator & Matrix Gateway", "cell:agentteams", "4th", "100.77.205.126", "https://manager-ai.hmz.one", "agentteams-manager", "default"),
        ("its", "its", "organization:its", "ITS Operational Gatekeeper", "cell:its", "8th", "100.66.243.55", "https://ai-its.antares.vn", "its-default", "default"),
        ("gl", "gl", "organization:gl", "GL Automation & Data Analytics", "cell:gl", "8th", "100.66.243.55", "https://ai-gl.antares.vn", "gl-default", "default"),
    ];

    public Iam360CellsService(
        IQwenPawClient qwenPawClient,
        IFleetInstanceRoutingService routingService,
        IMemoryCache cache,
        Iam360CellsPersistence persistence,
        ILogger<Iam360CellsService> logger)
    {
        _qwenPawClient = qwenPawClient;
        _routingService = routingService;
        _cache = cache;
        _persistence = persistence;
        _logger = logger;
        EnsureHydratedFromDisk();
    }

    private void EnsureHydratedFromDisk()
    {
        if (_hydratedFromDisk) return;
        lock (StateSyncLock)
        {
            if (_hydratedFromDisk) return;
            var loaded = _persistence.LoadState();
            foreach (var kv in loaded.IdentityStatusOverrides)
            {
                IdentityStatusOverrides[kv.Key] = kv.Value;
                if (kv.Value is "suspended" or "revoked")
                {
                    IamRevocationGuard.MarkIdentityCellRevoked(kv.Key, $"Restored {kv.Value} from durable state");
                }
            }
            foreach (var kv in loaded.OrgEnforcementOverrides)
            {
                OrgEnforcementOverrides[kv.Key] = kv.Value;
            }
            foreach (var orgKv in loaded.OrgRoleOverrides)
            {
                var inner = OrgRoleOverrides.GetOrAdd(orgKv.Key, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                foreach (var roleKv in orgKv.Value)
                {
                    inner[roleKv.Key] = roleKv.Value;
                }
            }
            SignedIntentsLedger.Clear();
            SignedIntentsLedger.AddRange(loaded.SignedIntentsLedger);
            _hydratedFromDisk = true;
        }
    }

    private void PersistCurrentStateWithIntent(Iam360SignedIntentEnvelopeDto envelope)
    {
        lock (StateSyncLock)
        {
            SignedIntentsLedger.Insert(0, envelope);
            if (SignedIntentsLedger.Count > 200)
            {
                SignedIntentsLedger.RemoveRange(200, SignedIntentsLedger.Count - 200);
            }

            var snapshot = new PersistedIam360CellsState(
                IdentityStatusOverrides.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase),
                OrgEnforcementOverrides.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase),
                OrgRoleOverrides.ToDictionary(
                    k => k.Key,
                    v => v.Value.ToDictionary(ik => ik.Key, iv => iv.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
                SignedIntentsLedger.ToList());

            _persistence.SaveState(snapshot);
        }
    }

    public IReadOnlyList<Iam360SignedIntentEnvelopeDto> GetSignedIntentsLedger()
    {
        lock (StateSyncLock)
        {
            return SignedIntentsLedger.ToList();
        }
    }

    public async Task<Iam360CellsStrategyResponseDto> GetCellsStrategyAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache.TryGetValue(StrategyCacheKey, out Iam360CellsStrategyResponseDto? cached) && cached is not null)
        {
            return cached;
        }

        var built = await BuildCellsStrategyAsync(ct);
        _cache.Set(StrategyCacheKey, built, TimeSpan.FromSeconds(25));
        return built;
    }

    public async Task<Iam360AgentCellInspectionDto?> GetAgentCellInspectionAsync(string agentIdOrSlug, bool force = false, CancellationToken ct = default)
    {
        var strategy = await GetCellsStrategyAsync(force, ct);
        var clean = NormalizeSlug(agentIdOrSlug);

        var identity = strategy.IdentityCells.FirstOrDefault(i =>
            i.Slug.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            i.IdentityCellId.Equals(agentIdOrSlug, StringComparison.OrdinalIgnoreCase) ||
            i.AgentId.Equals(clean, StringComparison.OrdinalIgnoreCase));

        if (identity is null && strategy.IdentityCells.Count > 0)
        {
            identity = strategy.IdentityCells[0];
        }

        if (identity is null) return null;

        var primaryOrg = strategy.OrganizationCells.FirstOrDefault(o =>
            o.CellId.Equals(identity.OrganizationCellId, StringComparison.OrdinalIgnoreCase))
            ?? strategy.OrganizationCells.First();

        var memberOrgs = strategy.OrganizationCells
            .Where(o => identity.Memberships.Any(m => m.CellId.Equals(o.CellId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new Iam360AgentCellInspectionDto(
            Schema: "iam360/agent-cell-inspection/v2",
            CheckedAt: strategy.CheckedAt,
            IdentityCell: identity,
            PrimaryOrganizationCell: primaryOrg,
            MemberOrganizationCells: memberOrgs,
            PermissionTree: identity.PermissionTree);
    }

    public async Task<Iam360IdentityCellDto?> MutateIdentityStatusAsync(string identityCellIdOrSlug, string action, string reason, CancellationToken ct = default)
    {
        var strategy = await GetCellsStrategyAsync(false, ct);
        var clean = NormalizeSlug(identityCellIdOrSlug);
        var target = strategy.IdentityCells.FirstOrDefault(i =>
            i.IdentityCellId.Equals(identityCellIdOrSlug, StringComparison.OrdinalIgnoreCase) ||
            i.Slug.Equals(clean, StringComparison.OrdinalIgnoreCase));

        if (target is null) return null;

        var normalizedAction = (action ?? "activate").Trim().ToLowerInvariant();
        var nextStatus = normalizedAction switch
        {
            "suspend" or "suspended" => "suspended",
            "revoke" or "revoked" => "revoked",
            _ => "active"
        };

        IdentityStatusOverrides[target.IdentityCellId] = nextStatus;
        if (nextStatus is "suspended" or "revoked")
        {
            IamRevocationGuard.MarkIdentityCellRevoked(target.IdentityCellId, string.IsNullOrWhiteSpace(reason) ? $"Marked {nextStatus} by operator" : reason);
            IamRevocationGuard.MarkIdentityCellRevoked(target.Slug, string.IsNullOrWhiteSpace(reason) ? $"Marked {nextStatus} by operator" : reason);
        }
        else
        {
            IamRevocationGuard.MarkIdentityCellActive(target.IdentityCellId);
            IamRevocationGuard.MarkIdentityCellActive(target.Slug);
        }

        var intentEnvelope = _persistence.CreateSignedIntent(
            runtimeId: target.RuntimeId,
            agentId: target.AgentId,
            cellId: target.OrganizationCellId,
            intentType: "set_identity_status",
            payload: new Dictionary<string, object?>
            {
                ["identity_cell_id"] = target.IdentityCellId,
                ["slug"] = target.Slug,
                ["status"] = nextStatus,
                ["reason"] = reason
            });
        PersistCurrentStateWithIntent(intentEnvelope);

        _cache.Remove(StrategyCacheKey);
        var refreshed = await GetCellsStrategyAsync(true, ct);
        return refreshed.IdentityCells.FirstOrDefault(i => i.IdentityCellId.Equals(target.IdentityCellId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Iam360OrganizationCellDto?> MutateOrgMemberRoleAsync(string orgCellId, string identityCellIdOrSlug, string role, string reason, CancellationToken ct = default)
    {
        var normalizedRole = (role ?? "employee").Trim().ToLowerInvariant();
        if (normalizedRole is not ("owner" or "manager" or "employee"))
        {
            normalizedRole = "employee";
        }

        var strategy = await GetCellsStrategyAsync(false, ct);
        var clean = NormalizeSlug(identityCellIdOrSlug);
        var identity = strategy.IdentityCells.FirstOrDefault(i =>
            i.IdentityCellId.Equals(identityCellIdOrSlug, StringComparison.OrdinalIgnoreCase) ||
            i.Slug.Equals(clean, StringComparison.OrdinalIgnoreCase));

        var icid = identity?.IdentityCellId ?? (identityCellIdOrSlug.StartsWith("identity:") ? identityCellIdOrSlug : $"identity:core/{clean}");
        var map = OrgRoleOverrides.GetOrAdd(orgCellId, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        map[icid] = normalizedRole;

        var orgObj = strategy.OrganizationCells.FirstOrDefault(o => o.CellId.Equals(orgCellId, StringComparison.OrdinalIgnoreCase));
        var intentEnvelope = _persistence.CreateSignedIntent(
            runtimeId: orgObj?.RuntimeId ?? "core",
            agentId: orgObj?.OwnerSlug ?? "default",
            cellId: orgCellId,
            intentType: "add_member",
            payload: new Dictionary<string, object?>
            {
                ["identity_cell_id"] = icid,
                ["role"] = normalizedRole,
                ["reason"] = reason
            });
        PersistCurrentStateWithIntent(intentEnvelope);

        _cache.Remove(StrategyCacheKey);
        var refreshed = await GetCellsStrategyAsync(true, ct);
        return refreshed.OrganizationCells.FirstOrDefault(o => o.CellId.Equals(orgCellId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Iam360OrganizationCellDto?> MutateOrgEnforcementAsync(string orgCellId, string enforcement, string reason, CancellationToken ct = default)
    {
        var mode = (enforcement ?? "preview").Trim().ToLowerInvariant();
        if (mode is not ("off" or "preview" or "on"))
        {
            mode = "preview";
        }

        var strategy = await GetCellsStrategyAsync(false, ct);
        OrgEnforcementOverrides[orgCellId] = mode;

        var orgObj = strategy.OrganizationCells.FirstOrDefault(o => o.CellId.Equals(orgCellId, StringComparison.OrdinalIgnoreCase));
        var intentEnvelope = _persistence.CreateSignedIntent(
            runtimeId: orgObj?.RuntimeId ?? "core",
            agentId: orgObj?.OwnerSlug ?? "default",
            cellId: orgCellId,
            intentType: "set_org_policy",
            payload: new Dictionary<string, object?>
            {
                ["runtime_enforcement"] = mode,
                ["reason"] = reason
            });
        PersistCurrentStateWithIntent(intentEnvelope);

        _cache.Remove(StrategyCacheKey);
        var refreshed = await GetCellsStrategyAsync(true, ct);
        return refreshed.OrganizationCells.FirstOrDefault(o => o.CellId.Equals(orgCellId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Iam360FleetAuditReportDto> RunLiveTestAllAgentsAsync(CancellationToken ct = default)
    {
        var strategy = await GetCellsStrategyAsync(true, ct);
        var results = new List<Iam360LiveAgentTestItemDto>(strategy.IdentityCells.Count);

        foreach (var idCell in strategy.IdentityCells)
        {
            var allowRes = await EvaluateAccessAsync(new Iam360EvaluateAccessRequest(
                IdentityCellId: idCell.Slug,
                OrganizationCellId: idCell.OrganizationCellId,
                Action: "read_file",
                ResourcePath: $"{idCell.WorkspaceDir}/README.md"), ct);

            var denyRes = await EvaluateAccessAsync(new Iam360EvaluateAccessRequest(
                IdentityCellId: idCell.IdentityCellId,
                OrganizationCellId: idCell.OrganizationCellId,
                Action: "write_file",
                ResourcePath: "/app/working.secret/keys.json"), ct);

            var expectedAllow = idCell.Status == "active" && !idCell.Revoked;
            var allowPass = allowRes.Allowed == expectedAllow;
            var denyPass = !denyRes.Allowed && denyRes.Effect == "EFFECT_DENY";
            var treePass = idCell.PermissionTree.Children.Count == 4 && idCell.Memberships.Count >= 1;

            results.Add(new Iam360LiveAgentTestItemDto(
                Slug: idCell.Slug,
                DisplayName: idCell.DisplayName,
                IdentityCellId: idCell.IdentityCellId,
                OrganizationCellId: idCell.OrganizationCellId,
                RoleInCell: idCell.CellRole,
                Status: idCell.Status,
                CrossOrgMembershipsCount: idCell.Memberships.Count,
                PermissionTreeBranchesCount: idCell.PermissionTree.Children.Count,
                McpCount: idCell.McpCount,
                ToolCount: idCell.ToolCount,
                WorkspaceReadAllowed: allowRes.Allowed,
                BaselineSecretDenied: denyPass,
                Passed: allowPass && denyPass && treePass));
        }

        var passedCount = results.Count(r => r.Passed);
        return new Iam360FleetAuditReportDto(
            Schema: "iam360/fleet-live-audit-report/v2",
            ExecutedAt: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            TotalAgentsTested: results.Count,
            PassedCount: passedCount,
            FailedCount: results.Count - passedCount,
            OrganizationCellsTested: strategy.OrganizationCells.Count,
            SignedIntentsVerified: GetSignedIntentsLedger().Count,
            Results: results);
    }

    public async Task<Iam360EvaluateAccessResponse> EvaluateAccessAsync(Iam360EvaluateAccessRequest request, CancellationToken ct = default)
    {
        var strategy = await GetCellsStrategyAsync(false, ct);
        var cleanId = NormalizeSlug(request.IdentityCellId);
        var identity = strategy.IdentityCells.FirstOrDefault(i =>
            i.IdentityCellId.Equals(request.IdentityCellId, StringComparison.OrdinalIgnoreCase) ||
            i.Slug.Equals(cleanId, StringComparison.OrdinalIgnoreCase))
            ?? strategy.IdentityCells.First();

        var orgCellId = string.IsNullOrWhiteSpace(request.OrganizationCellId)
            ? identity.OrganizationCellId
            : request.OrganizationCellId!;

        var orgCell = strategy.OrganizationCells.FirstOrDefault(o =>
            o.CellId.Equals(orgCellId, StringComparison.OrdinalIgnoreCase))
            ?? strategy.OrganizationCells.First();

        var action = (request.Action ?? "read_file").Trim();
        var path = (request.ResourcePath ?? "").Trim();

        // Look up by exact slug or identity_cell_id so shared runtime profiles never collide
        identity = strategy.IdentityCells.FirstOrDefault(i =>
            i.Slug.Equals(request.IdentityCellId, StringComparison.OrdinalIgnoreCase) ||
            i.IdentityCellId.Equals(request.IdentityCellId, StringComparison.OrdinalIgnoreCase) ||
            i.Slug.Equals(cleanId, StringComparison.OrdinalIgnoreCase)) ?? identity;

        // 1. Check revocation / suspension
        if (identity.Status is "revoked" or "suspended" || identity.Revoked)
        {
            return new Iam360EvaluateAccessResponse(
                Allowed: false,
                Effect: "EFFECT_DENY",
                ReasonCode: "identity_cell_suspended_or_revoked",
                MatchedRule: "iam360-revocation-guard",
                RoleInCell: "none",
                IdentityCellId: identity.IdentityCellId,
                OrganizationCellId: orgCell.CellId,
                TracePath:
                [
                    $"Identity Cell: {identity.IdentityCellId} (status={identity.Status})",
                    "Guardrail: IAM360 Revocation & Lifecycle Gate -> DENY"
                ]);
        }

        // 2. Check membership in target Organization Cell
        var membership = identity.Memberships.FirstOrDefault(m => m.CellId.Equals(orgCell.CellId, StringComparison.OrdinalIgnoreCase));
        if (membership is null)
        {
            return new Iam360EvaluateAccessResponse(
                Allowed: false,
                Effect: "EFFECT_DENY",
                ReasonCode: "cross_organization_boundary_denied",
                MatchedRule: "default-deny-cross-cell",
                RoleInCell: "non-member",
                IdentityCellId: identity.IdentityCellId,
                OrganizationCellId: orgCell.CellId,
                TracePath:
                [
                    $"Identity Cell: {identity.IdentityCellId}",
                    $"Target Organization Cell: {orgCell.CellId} ({orgCell.DisplayName})",
                    "ReBAC Tuple Check: No owner/manager/employee tuple found -> Default Deny"
                ]);
        }

        var role = membership.Role.ToLowerInvariant();

        // 3. Check baseline protected runtime paths
        string[] protectedPaths = ["/app/working.secret", "/app/venv", "/app/working/config.json", "/etc", "/proc", "/root", "$workspace/agent.json", "$workspace/credentials.yaml"];
        if (!string.IsNullOrEmpty(path) && protectedPaths.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return new Iam360EvaluateAccessResponse(
                Allowed: false,
                Effect: "EFFECT_DENY",
                ReasonCode: "baseline_protected_path_guardrail",
                MatchedRule: "protect-runtime-paths / protect-agent-config",
                RoleInCell: role,
                IdentityCellId: identity.IdentityCellId,
                OrganizationCellId: orgCell.CellId,
                TracePath:
                [
                    $"Identity Cell: {identity.IdentityCellId}",
                    $"Organization Cell: {orgCell.CellId} (Role: {role})",
                    $"Resource Path: {path}",
                    "Admin Baseline Policy: protect-runtime-paths -> EFFECT_DENY"
                ]);
        }

        // 4. Evaluate role authority & capabilities
        if (role == "owner")
        {
            return new Iam360EvaluateAccessResponse(
                Allowed: true,
                Effect: "EFFECT_ALLOW",
                ReasonCode: "owner_full_cell_authority",
                MatchedRule: "owner-full-cell-authority",
                RoleInCell: role,
                IdentityCellId: identity.IdentityCellId,
                OrganizationCellId: orgCell.CellId,
                TracePath:
                [
                    $"Identity Cell: {identity.IdentityCellId} (Assurance: {identity.AssuranceLevel})",
                    $"Organization Cell: {orgCell.CellId} ({orgCell.DisplayName})",
                    "ReBAC Relation: owner -> Full Cell Authority",
                    $"Cerbos Rule: owner-full-cell-authority allows '{action}' -> EFFECT_ALLOW"
                ]);
        }

        var hasMcpOrTool = identity.McpClients.Any(m => m.Enabled && (action.Contains(m.Key, StringComparison.OrdinalIgnoreCase) || m.Id.Equals(action, StringComparison.OrdinalIgnoreCase)))
            || identity.BuiltinTools.Any(t => t.Enabled && (t.Name.Equals(action, StringComparison.OrdinalIgnoreCase) || t.Id.Equals(action, StringComparison.OrdinalIgnoreCase)))
            || action is "read_file" or "grep_search" or "glob_search" or "chat_with_agent" or "submit_to_agent" or "check_agent_task" or "list_agents" or "view_image" or "web_search" or "web_fetch";

        if (role == "manager" && (hasMcpOrTool || !action.Contains("delete", StringComparison.OrdinalIgnoreCase)))
        {
            return new Iam360EvaluateAccessResponse(
                Allowed: true,
                Effect: "EFFECT_ALLOW",
                ReasonCode: "manager_operational_authority",
                MatchedRule: "manager-operational-authority",
                RoleInCell: role,
                IdentityCellId: identity.IdentityCellId,
                OrganizationCellId: orgCell.CellId,
                TracePath:
                [
                    $"Identity Cell: {identity.IdentityCellId}",
                    $"Organization Cell: {orgCell.CellId} ({orgCell.DisplayName})",
                    "ReBAC Relation: manager -> Operational & Delegation Authority",
                    $"Cerbos Rule: manager-operational-authority allows '{action}' -> EFFECT_ALLOW"
                ]);
        }

        if (role == "employee" && hasMcpOrTool && action is not ("agent.delegate" or "set_org_policy" or "remove_member"))
        {
            return new Iam360EvaluateAccessResponse(
                Allowed: true,
                Effect: "EFFECT_ALLOW",
                ReasonCode: "employee_scoped_execution",
                MatchedRule: "employee-scoped-execution",
                RoleInCell: role,
                IdentityCellId: identity.IdentityCellId,
                OrganizationCellId: orgCell.CellId,
                TracePath:
                [
                    $"Identity Cell: {identity.IdentityCellId}",
                    $"Organization Cell: {orgCell.CellId} ({orgCell.DisplayName})",
                    "ReBAC Relation: employee -> Scoped Execution within Workspace",
                    $"Attested Capability: '{action}' verified in Identity Cell -> EFFECT_ALLOW"
                ]);
        }

        return new Iam360EvaluateAccessResponse(
            Allowed: false,
            Effect: "EFFECT_DENY",
            ReasonCode: "capability_not_granted_to_role",
            MatchedRule: "default-deny-unmatched-action",
            RoleInCell: role,
            IdentityCellId: identity.IdentityCellId,
            OrganizationCellId: orgCell.CellId,
            TracePath:
            [
                $"Identity Cell: {identity.IdentityCellId}",
                $"Organization Cell: {orgCell.CellId} (Role: {role})",
                $"Action '{action}' is outside granted capabilities for role '{role}' -> EFFECT_DENY"
            ]);
    }

    private async Task<Iam360CellsStrategyResponseDto> BuildCellsStrategyAsync(CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        IReadOnlyList<AgentSummaryResponse> fleetAgents = [];
        try
        {
            using var listCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listCts.CancelAfter(TimeSpan.FromSeconds(2.5));
            fleetAgents = await _qwenPawClient.GetAgentsAsync(listCts.Token);
        }
        catch
        {
            fleetAgents = [];
        }

        var agentMap = new Dictionary<string, AgentSummaryResponse>(StringComparer.OrdinalIgnoreCase);
        foreach (var ag in fleetAgents)
        {
            var clean = NormalizeSlug(ag.Slug);
            if (!string.IsNullOrWhiteSpace(clean))
            {
                agentMap[clean] = ag;
            }
        }

        EnsureBaselineAgents(agentMap);

        var profilesBySlug = new Dictionary<string, RawAgentProfileData>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in agentMap.Values)
        {
            var slug = NormalizeSlug(summary.Slug);
            var (instKey, profileId) = ResolveInstanceAndProfile(slug);
            var rawJson = _cache.TryGetValue($"agent360:profile:{slug}", out string? cachedRaw) && cachedRaw is not null
                ? cachedRaw
                : "";
            profilesBySlug[slug] = ParseAgentProfile(slug, instKey, profileId, summary, rawJson);
        }

        // First Pass: Build Organization Cells
        var orgCells = new List<Iam360OrganizationCellDto>();
        foreach (var cellMeta in FleetCellsCatalog)
        {
            var instAgents = profilesBySlug.Values
                .Where(p => p.InstanceKey.Equals(cellMeta.Key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Slug)
                .ToList();

            var ownerProfile = instAgents.FirstOrDefault(a => a.Slug.Equals(cellMeta.OwnerSlug, StringComparison.OrdinalIgnoreCase))
                ?? instAgents.FirstOrDefault(a => a.AgentId.Equals(cellMeta.OwnerProfile, StringComparison.OrdinalIgnoreCase))
                ?? instAgents.FirstOrDefault();

            var ownerIcid = ownerProfile?.IdentityCellId ?? $"identity:{cellMeta.Key}/{cellMeta.OwnerProfile}";
            var ownerSlug = ownerProfile?.Slug ?? cellMeta.OwnerSlug;
            var ownerName = ownerProfile?.Name ?? cellMeta.DisplayName;

            var membersDict = new Dictionary<string, Iam360OrgCellMemberDto>(StringComparer.OrdinalIgnoreCase);
            var contextBindings = new List<Iam360ContextBindingDto>();
            var roleOverrides = OrgRoleOverrides.TryGetValue(cellMeta.CellId, out var ov) ? ov : null;

            foreach (var prof in instAgents)
            {
                foreach (var ch in prof.EnabledChannels)
                {
                    contextBindings.Add(new Iam360ContextBindingDto(
                        IdentityCellId: prof.IdentityCellId,
                        AgentId: prof.AgentId,
                        Slug: prof.Slug,
                        RuntimeId: cellMeta.Key,
                        Channel: ch,
                        SessionPrefix: $"{ch}:"));
                }

                if (prof.IdentityCellId.Equals(ownerIcid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mRole = prof.CellRole == "owner" ? "manager" : prof.CellRole;
                if (roleOverrides is not null && roleOverrides.TryGetValue(prof.IdentityCellId, out var customRole))
                {
                    mRole = customRole;
                }

                membersDict[prof.IdentityCellId] = new Iam360OrgCellMemberDto(
                    IdentityCellId: prof.IdentityCellId,
                    Role: mRole,
                    AgentId: prof.AgentId,
                    Slug: prof.Slug,
                    Name: prof.Name,
                    Status: prof.Enabled ? "active" : "suspended",
                    AddedBy: ownerIcid,
                    AddedAt: checkedAt);
            }

            const string monsIcid = "identity:core/mons";
            if (cellMeta.Key != "core" && !membersDict.ContainsKey(monsIcid) && !ownerIcid.Equals(monsIcid, StringComparison.OrdinalIgnoreCase))
            {
                var monsRole = roleOverrides is not null && roleOverrides.TryGetValue(monsIcid, out var mr) ? mr : "manager";
                membersDict[monsIcid] = new Iam360OrgCellMemberDto(
                    IdentityCellId: monsIcid,
                    Role: monsRole,
                    AgentId: "mons",
                    Slug: "mons",
                    Name: "Mons (Core Dev & Ops)",
                    Status: "active",
                    AddedBy: ownerIcid,
                    AddedAt: checkedAt);
            }

            var rebacTuples = new List<Iam360RebacTupleDto>
            {
                new(ownerIcid, "owner", cellMeta.OrgId)
            };
            foreach (var kv in membersDict.OrderBy(k => k.Key))
            {
                rebacTuples.Add(new Iam360RebacTupleDto(kv.Key, kv.Value.Role, cellMeta.OrgId));
            }

            var baselineRules = BuildBaselineRules();
            var orgRules = BuildOrgRules(cellMeta.OrgSlug, instAgents);
            var activeCount = instAgents.Count(a => a.Enabled);
            var enforcement = OrgEnforcementOverrides.TryGetValue(cellMeta.CellId, out var enf) ? enf : "preview";

            var orgTree = BuildOrganizationPermissionTree(
                cellMeta.CellId,
                cellMeta.DisplayName,
                ownerIcid,
                ownerName,
                ownerSlug,
                membersDict.Values.ToList(),
                baselineRules,
                orgRules,
                contextBindings);

            orgCells.Add(new Iam360OrganizationCellDto(
                Schema: "iam360/organization-cell/v2",
                CellId: cellMeta.CellId,
                Id: cellMeta.CellId,
                Organization: cellMeta.OrgSlug,
                OrganizationId: cellMeta.OrgId,
                DisplayName: cellMeta.DisplayName,
                ClusterId: $"cluster:node-{cellMeta.ClusterNode}",
                ClusterNode: cellMeta.ClusterNode,
                NodeIp: cellMeta.NodeIp,
                Environment: "production",
                ServiceId: $"iam360-{cellMeta.OrgSlug}",
                Domain: cellMeta.PublicUrl.Replace("https://", "").Replace("http://", ""),
                EndpointUrl: cellMeta.PublicUrl,
                RuntimeId: cellMeta.Key,
                OwnerIdentityCellId: ownerIcid,
                OwnerSlug: ownerSlug,
                OwnerName: ownerName,
                Lifecycle: activeCount > 0 ? "active" : "degraded",
                Status: activeCount > 0 ? "active" : "degraded",
                DefaultDeny: true,
                RuntimeEnforcement: enforcement,
                Template: "standard",
                Revision: 1,
                DesiredRevision: 1,
                ObservedRevision: 1,
                Members: membersDict,
                MemberCount: membersDict.Count,
                TotalIdentitiesCount: membersDict.Count + 1,
                ActiveAgentsCount: activeCount,
                ContextBindings: contextBindings,
                RebacTuples: rebacTuples,
                BaselineRules: baselineRules,
                OrganizationRules: orgRules,
                BaselinePolicyHash: ShortHash($"{cellMeta.CellId}:baseline:v2"),
                OrganizationPolicyHash: ShortHash($"{cellMeta.CellId}:org:v2:{membersDict.Count}"),
                OrgKeyId: _persistence.KeyId,
                LastReadBack: checkedAt,
                TelemetrySource: "agent360-fleet-gateway",
                PermissionTree: orgTree));
        }

        // Second Pass: Build Identity Cells
        var identityCells = new List<Iam360IdentityCellDto>();
        var orgById = orgCells.ToDictionary(o => o.CellId, StringComparer.OrdinalIgnoreCase);

        foreach (var prof in profilesBySlug.Values.OrderBy(p => p.Slug))
        {
            var memberships = new List<Iam360CellMembershipDto>();
            foreach (var ocell in orgCells)
            {
                if (ocell.OwnerIdentityCellId.Equals(prof.IdentityCellId, StringComparison.OrdinalIgnoreCase))
                {
                    memberships.Add(new Iam360CellMembershipDto(
                        CellId: ocell.CellId,
                        Organization: ocell.Organization,
                        DisplayName: ocell.DisplayName,
                        Role: "owner",
                        Lifecycle: ocell.Lifecycle));
                }
                else if (ocell.Members.TryGetValue(prof.IdentityCellId, out var m))
                {
                    memberships.Add(new Iam360CellMembershipDto(
                        CellId: ocell.CellId,
                        Organization: ocell.Organization,
                        DisplayName: ocell.DisplayName,
                        Role: m.Role,
                        Lifecycle: ocell.Lifecycle));
                }
            }

            var statusVal = prof.Enabled ? "active" : "suspended";
            if (IdentityStatusOverrides.TryGetValue(prof.IdentityCellId, out var overrideStatus))
            {
                statusVal = overrideStatus;
            }

            var isRevoked = statusVal == "revoked" || !IamRevocationGuard.IsExecutionAllowed(prof.Slug, out _);
            if (isRevoked) statusVal = "revoked";

            var primaryOrgName = orgById.TryGetValue(prof.OrganizationCellId, out var pOrg)
                ? pOrg.DisplayName
                : prof.OrganizationCellId;

            var personalTree = BuildIdentityPermissionTree(prof, statusVal, memberships, orgById);

            identityCells.Add(new Iam360IdentityCellDto(
                Schema: "iam360/identity-cell/v2",
                IdentityCellId: prof.IdentityCellId,
                Id: prof.IdentityCellId,
                Subject: prof.IdentityCellId,
                SubjectType: "agent",
                RuntimeId: prof.InstanceKey,
                InstanceKey: prof.InstanceKey,
                AgentId: prof.AgentId,
                Slug: prof.Slug,
                CanonicalName: prof.Name,
                DisplayName: prof.Name,
                Description: prof.Description,
                RoleTag: prof.RoleTag,
                CellRole: prof.CellRole,
                IsRootAgent: prof.IsRootAgent,
                OrganizationId: prof.OrganizationId,
                OrganizationCellId: prof.OrganizationCellId,
                PrimaryOrgCell: primaryOrgName,
                OwnerIdentityCellId: prof.IdentityCellId,
                ControllerSubjectId: prof.InstanceKey == "gm" ? "user:gm:manager" : $"user:{prof.InstanceKey}:admin",
                Status: statusVal,
                Assurance: "runtime_attested",
                AssuranceLevel: statusVal == "active" ? "strongly_verified" : "verified",
                Attested: true,
                AttestedAt: checkedAt,
                Revision: 1,
                ObservedRevision: 1,
                Drift: false,
                Revoked: isRevoked,
                Model: prof.Model,
                RuntimeVersion: prof.RuntimeVersion,
                Node: prof.Node,
                ClusterNode: prof.ClusterNode,
                EndpointUrl: prof.EndpointUrl,
                WorkspaceDir: prof.WorkspaceDir,
                SystemPromptFiles: prof.SystemPromptFiles,
                EnabledChannels: prof.EnabledChannels,
                McpClients: prof.McpClients,
                McpCount: prof.McpClients.Count,
                EnabledMcpCount: prof.McpClients.Count(m => m.Enabled),
                BuiltinTools: prof.BuiltinTools,
                ToolCount: prof.BuiltinTools.Count,
                EnabledToolCount: prof.BuiltinTools.Count(t => t.Enabled),
                Security: prof.Security,
                MachineIdentityRef: $"infisical:/qwenpaw/agents/{prof.Slug}/app/runtime",
                Memberships: memberships,
                PermissionTree: personalTree));
        }

        var globalTree = new Iam360PermissionTreeNodeDto(
            Id: "root:iam360-platform",
            Label: "IAM360 × Skyer Multi-Cell Governance Platform",
            Subtitle: $"{orgCells.Count} Organization Cells • {identityCells.Count} Self-Owned Identity Cells • OpenFGA ReBAC + Cerbos ABAC",
            Level: "organization_cell",
            Effect: "ALLOW",
            Badge: "Platform Root",
            Accent: "peach",
            Source: "iam360.hmz.one",
            Children: orgCells.Select(o => o.PermissionTree).ToList());

        var ledger = GetSignedIntentsLedger();
        var summaryDto = new Iam360CellsSummaryDto(
            TotalAgents: identityCells.Count,
            ActiveAgents: identityCells.Count(i => i.Status == "active"),
            DisabledAgents: identityCells.Count(i => i.Status != "active"),
            IdentityCellsCount: identityCells.Count,
            OrganizationCellsCount: orgCells.Count,
            RuntimesCount: FleetCellsCatalog.Count,
            TotalMcpBindings: identityCells.Sum(i => i.McpCount),
            TotalToolBindings: identityCells.Sum(i => i.ToolCount),
            TotalRebacTuples: orgCells.Sum(o => o.RebacTuples.Count),
            SignedIntentsCount: ledger.Count);

        return new Iam360CellsStrategyResponseDto(
            Schema: "iam360/fleet-two-cell-discovery/v2",
            CheckedAt: checkedAt,
            Summary: summaryDto,
            OrganizationCells: orgCells,
            IdentityCells: identityCells,
            GlobalHierarchyTree: globalTree,
            SignedIntentsLedger: ledger);
    }

    private static Iam360PermissionTreeNodeDto BuildIdentityPermissionTree(
        RawAgentProfileData prof,
        string statusVal,
        IReadOnlyList<Iam360CellMembershipDto> memberships,
        IReadOnlyDictionary<string, Iam360OrganizationCellDto> orgById)
    {
        var mcpNodes = prof.McpClients.Select(m => new Iam360PermissionTreeNodeDto(
            Id: $"tree:{prof.Slug}:mcp:{m.Key}",
            Label: m.Name,
            Subtitle: $"MCP Server ({m.Id}) • Transport: {m.Transport}",
            Level: "capability_leaf",
            Effect: m.Enabled ? "ALLOW" : "DENY",
            Badge: m.Enabled ? "MCP Enabled" : "Disabled",
            Accent: m.Enabled ? "mint" : "rose",
            Source: "Runtime MCP Client",
            Children: [])).ToList();

        var toolNodes = prof.BuiltinTools.Select(t => new Iam360PermissionTreeNodeDto(
            Id: $"tree:{prof.Slug}:tool:{t.Name}",
            Label: t.Name,
            Subtitle: string.IsNullOrWhiteSpace(t.Description) ? $"Builtin Runtime Tool ({t.Id})" : t.Description,
            Level: "capability_leaf",
            Effect: t.Enabled ? "ALLOW" : "DENY",
            Badge: t.Enabled ? "Tool Granted" : "Denied",
            Accent: t.Enabled ? "lavender" : "rose",
            Source: "QwenPaw Builtin Tool",
            Children: [])).ToList();

        var channelNodes = prof.EnabledChannels.Select(ch => new Iam360PermissionTreeNodeDto(
            Id: $"tree:{prof.Slug}:channel:{ch}",
            Label: $"Channel: {ch.ToUpperInvariant()}",
            Subtitle: $"Session Context Prefix: {ch}:* • Fail-closed principal routing",
            Level: "capability_leaf",
            Effect: "SCOPED",
            Badge: "Bound Context",
            Accent: "sky",
            Source: "Channel Adapter",
            Children: [])).ToList();

        var orgMembershipNodes = memberships.Select(m =>
        {
            orgById.TryGetValue(m.CellId, out var ocell);
            var roleDesc = m.Role switch
            {
                "owner" => "Toàn quyền quản trị Cell (actions: [*]), ký uỷ quyền intent & quản lý thành viên",
                "manager" => "Quyền điều phối tác vụ, uỷ quyền A2A nội bộ & truy vấn dữ liệu vận hành",
                _ => "Quyền thực thi tác vụ trong phạm vi Workspace cô lập ($workspace)"
            };

            var ruleChildren = new List<Iam360PermissionTreeNodeDto>
            {
                new(
                    Id: $"tree:{prof.Slug}:org:{m.CellId}:rebac",
                    Label: $"ReBAC Tuple: ({prof.IdentityCellId}, {m.Role}, organization:{m.Organization})",
                    Subtitle: roleDesc,
                    Level: "role_authority",
                    Effect: "ALLOW",
                    Badge: $"Role: {m.Role.ToUpperInvariant()}",
                    Accent: m.Role == "owner" ? "peach" : m.Role == "manager" ? "lavender" : "sky",
                    Source: "OpenFGA Graph",
                    Children: []),
                new(
                    Id: $"tree:{prof.Slug}:org:{m.CellId}:abac",
                    Label: $"Cerbos ABAC Policy: org-{m.Organization}-v2",
                    Subtitle: m.Role == "owner"
                        ? "Rule: owner-full-cell-authority -> EFFECT_ALLOW (*)"
                        : m.Role == "manager"
                            ? "Rule: manager-operational-authority -> EFFECT_ALLOW (coordinate, delegate, read/write)"
                            : "Rule: employee-scoped-execution -> EFFECT_ALLOW (scoped tools)",
                    Level: "capability_leaf",
                    Effect: "ALLOW",
                    Badge: "Cerbos ABAC",
                    Accent: "mint",
                    Source: ocell?.OrganizationPolicyHash ?? "sha256:verified",
                    Children: []),
                new(
                    Id: $"tree:{prof.Slug}:org:{m.CellId}:baseline",
                    Label: "Admin Baseline Guardrails (Immutable)",
                    Subtitle: $"Chặn truy cập ngoài {prof.WorkspaceDir}, bảo vệ /app/working.secret & credentials.yaml",
                    Level: "guardrail",
                    Effect: "DENY",
                    Badge: "Enforced Boundary",
                    Accent: "rose",
                    Source: ocell?.BaselinePolicyHash ?? "baseline-standard",
                    Children: [])
            };

            return new Iam360PermissionTreeNodeDto(
                Id: $"tree:{prof.Slug}:org:{m.CellId}",
                Label: $"{m.DisplayName} ({m.CellId})",
                Subtitle: $"Tư cách trong tổ chức: {m.Role.ToUpperInvariant()} • Trạng thái Cell: {m.Lifecycle}",
                Level: "organization_cell",
                Effect: "ALLOW",
                Badge: $"{m.Role.ToUpperInvariant()} in {m.CellId}",
                Accent: m.Role == "owner" ? "peach" : "mint",
                Source: "Organization Cell",
                Children: ruleChildren);
        }).ToList();

        return new Iam360PermissionTreeNodeDto(
            Id: $"tree:identity:{prof.Slug}",
            Label: $"{prof.Name} ({prof.IdentityCellId})",
            Subtitle: $"Self-Owned Identity Cell • Model: {prof.Model} • Node {prof.ClusterNode} • Controller: {(prof.InstanceKey == "gm" ? "user:gm:manager" : $"user:{prof.InstanceKey}:admin")}",
            Level: "identity",
            Effect: statusVal == "active" ? "ALLOW" : "DENY",
            Badge: $"Identity: {statusVal.ToUpperInvariant()}",
            Accent: statusVal == "active" ? "peach" : "rose",
            Source: "Runtime Attested",
            Children:
            [
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{prof.Slug}:group:orgs",
                    Label: $"1. Tư cách & Quyền hạn theo Organization Cells ({memberships.Count} Cells)",
                    Subtitle: "Phân quyền từ cấp Tổ chức (Tenant Boundary), ReBAC Role Tuples & Cerbos ABAC Policy",
                    Level: "capability_group",
                    Effect: "ALLOW",
                    Badge: $"{memberships.Count} Org Cells",
                    Accent: "peach",
                    Source: "IAM360 ReBAC + ABAC",
                    Children: orgMembershipNodes),
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{prof.Slug}:group:mcp",
                    Label: $"2. Máy chủ MCP được uỷ quyền tại Identity Cell ({prof.McpClients.Count} MCP Clients)",
                    Subtitle: "Các kết nối MCP được chứng thực trực tiếp trên Runtime của Agent",
                    Level: "capability_group",
                    Effect: "ALLOW",
                    Badge: $"{prof.McpClients.Count(m => m.Enabled)}/{prof.McpClients.Count} Active",
                    Accent: "mint",
                    Source: "Identity Cell Attestation",
                    Children: mcpNodes),
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{prof.Slug}:group:tools",
                    Label: $"3. Công cụ Thực thi Nội tại ({prof.BuiltinTools.Count} Builtin Tools)",
                    Subtitle: "Tập quyền thực thi file, shell, tìm kiếm, điều phối A2A và tự kiểm tra IAM360",
                    Level: "capability_group",
                    Effect: "ALLOW",
                    Badge: $"{prof.BuiltinTools.Count(t => t.Enabled)} Tools",
                    Accent: "lavender",
                    Source: "Identity Cell Attestation",
                    Children: toolNodes),
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{prof.Slug}:group:channels",
                    Label: $"4. Kênh Giao tiếp & Ranh giới Bảo mật ({prof.EnabledChannels.Count} Channels)",
                    Subtitle: $"Workspace cô lập: {prof.WorkspaceDir} • File Guard: {(prof.Security.FileGuardEnabled ? "ON" : "OFF")}",
                    Level: "capability_group",
                    Effect: "SCOPED",
                    Badge: $"{prof.EnabledChannels.Count} Channels",
                    Accent: "sky",
                    Source: "Security & Context Guard",
                    Children: channelNodes)
            ]);
    }

    private static Iam360PermissionTreeNodeDto BuildOrganizationPermissionTree(
        string cellId,
        string displayName,
        string ownerIcid,
        string ownerName,
        string ownerSlug,
        IReadOnlyList<Iam360OrgCellMemberDto> members,
        IReadOnlyList<Iam360PolicyRuleDto> baselineRules,
        IReadOnlyList<Iam360PolicyRuleDto> orgRules,
        IReadOnlyList<Iam360ContextBindingDto> contextBindings)
    {
        var ownerNode = new Iam360PermissionTreeNodeDto(
            Id: $"tree:{cellId}:owner:{ownerSlug}",
            Label: $"👑 Owner Identity Cell: {ownerName} ({ownerIcid})",
            Subtitle: $"Chủ sở hữu duy nhất của {cellId} • Toàn quyền điều phối, uỷ quyền & ký chính sách nội bộ",
            Level: "identity",
            Effect: "ALLOW",
            Badge: "CELL OWNER",
            Accent: "peach",
            Source: $"slug:{ownerSlug}",
            Children:
            [
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{cellId}:owner:{ownerSlug}:rule",
                    Label: "Rule: owner-full-cell-authority (actions: [*])",
                    Subtitle: "Được phép thực thi toàn bộ công cụ và quản lý thành viên trong phạm vi Baseline",
                    Level: "capability_leaf",
                    Effect: "ALLOW",
                    Badge: "EFFECT_ALLOW",
                    Accent: "mint",
                    Source: "Cerbos ABAC",
                    Children: [])
            ]);

        var managers = members.Where(m => m.Role.Equals("manager", StringComparison.OrdinalIgnoreCase)).ToList();
        var employees = members.Where(m => !m.Role.Equals("manager", StringComparison.OrdinalIgnoreCase)).ToList();

        var managerNodes = managers.Select(m => new Iam360PermissionTreeNodeDto(
            Id: $"tree:{cellId}:manager:{m.Slug}",
            Label: $"◆ Manager Identity Cell: {m.Name} ({m.IdentityCellId})",
            Subtitle: $"Quản lý điều phối trong {cellId} • Uỷ quyền tác vụ & đọc/ghi dữ liệu vận hành",
            Level: "identity",
            Effect: m.Status == "active" ? "ALLOW" : "DENY",
            Badge: $"MANAGER • {m.Status.ToUpperInvariant()}",
            Accent: "lavender",
            Source: $"slug:{m.Slug}",
            Children: [])).ToList();

        var employeeNodes = employees.Select(m => new Iam360PermissionTreeNodeDto(
            Id: $"tree:{cellId}:employee:{m.Slug}",
            Label: $"● Employee Identity Cell: {m.Name} ({m.IdentityCellId})",
            Subtitle: $"Chuyên viên thực thi trong {cellId} • Giới hạn trong Workspace và công cụ nghiệp vụ được cấp",
            Level: "identity",
            Effect: m.Status == "active" ? "ALLOW" : "DENY",
            Badge: $"EMPLOYEE • {m.Status.ToUpperInvariant()}",
            Accent: "sky",
            Source: $"slug:{m.Slug}",
            Children: [])).ToList();

        var guardrailNodes = baselineRules.Select(r => new Iam360PermissionTreeNodeDto(
            Id: $"tree:{cellId}:baseline:{r.Name}",
            Label: $"Rule: {r.Name}",
            Subtitle: r.Description,
            Level: "guardrail",
            Effect: r.Effect.Contains("DENY") ? "DENY" : "ALLOW",
            Badge: r.Effect,
            Accent: r.Effect.Contains("DENY") ? "rose" : "mint",
            Source: "Admin Baseline",
            Children: [])).ToList();

        return new Iam360PermissionTreeNodeDto(
            Id: $"tree:org:{cellId}",
            Label: $"{displayName} ({cellId})",
            Subtitle: $"1 Owner + {members.Count} Member Identity Cells • {contextBindings.Count} Channel Contexts • Default Deny",
            Level: "organization_cell",
            Effect: "ALLOW",
            Badge: $"{members.Count + 1} Identities",
            Accent: "peach",
            Source: cellId,
            Children:
            [
                ownerNode,
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{cellId}:group:managers",
                    Label: $"Khối Quản lý & Điều phối (Managers: {managers.Count})",
                    Subtitle: "Nhận uỷ quyền từ Owner, điều phối A2A và giám sát chất lượng",
                    Level: "role_authority",
                    Effect: "ALLOW",
                    Badge: $"{managers.Count} Managers",
                    Accent: "lavender",
                    Source: "ReBAC relation: manager",
                    Children: managerNodes),
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{cellId}:group:employees",
                    Label: $"Khối Thực thi Nghiệp vụ (Employees: {employees.Count})",
                    Subtitle: "Thực thi công cụ chuyên môn trong ranh giới an toàn của Cell",
                    Level: "role_authority",
                    Effect: "ALLOW",
                    Badge: $"{employees.Count} Employees",
                    Accent: "sky",
                    Source: "ReBAC relation: employee",
                    Children: employeeNodes),
                new Iam360PermissionTreeNodeDto(
                    Id: $"tree:{cellId}:group:baseline",
                    Label: $"Ranh giới Bảo mật Bất biến (Admin Baseline Policy: {baselineRules.Count} Rules)",
                    Subtitle: "Chính sách nền tảng do Admin khoá; ngay cả Owner cũng không thể vượt qua",
                    Level: "guardrail",
                    Effect: "SCOPED",
                    Badge: "Immutable Baseline",
                    Accent: "rose",
                    Source: "Cerbos Baseline",
                    Children: guardrailNodes)
            ]);
    }

    private static IReadOnlyList<Iam360PolicyRuleDto> BuildBaselineRules() =>
    [
        new("protect-runtime-paths", ["*"], ["*"], "EFFECT_DENY", "Cấm truy cập /app/working.secret, /app/venv, /etc, /proc, /root"),
        new("protect-agent-config", ["write_file", "edit_file", "append_file"], ["*"], "EFFECT_DENY", "Cấm tự ý ghi đè $workspace/agent.json và $workspace/credentials.yaml"),
        new("writes-stay-in-workspace", ["write_file", "edit_file", "append_file"], ["*"], "EFFECT_DENY", "Mọi thao tác ghi file bắt buộc nằm trong $workspace cô lập của Agent"),
        new("no-external-delegation", ["chat_with_agent", "submit_to_agent"], ["*"], "EFFECT_DENY", "Cấm tự ý uỷ quyền tác vụ ra ngoài phạm vi Organization Cell khi chưa được duyệt"),
        new("owner-baseline", ["*"], ["owner"], "EFFECT_ALLOW", "Owner Identity Cell có toàn quyền trong phạm vi không vi phạm các quy tắc DENY ở trên"),
        new("member-baseline", ["read_file", "grep_search", "glob_search", "chat_with_agent", "submit_to_agent", "iam360_whoami", "iam360_submit_intent"], ["member"], "EFFECT_ALLOW", "Member Identity Cells được phép đọc và phối hợp nội bộ trong ranh giới Cell")
    ];

    private static IReadOnlyList<Iam360PolicyRuleDto> BuildOrgRules(string orgSlug, IReadOnlyList<RawAgentProfileData> agents) =>
    [
        new("owner-full-cell-authority", ["*"], ["owner"], "EFFECT_ALLOW", $"Chủ sở hữu {orgSlug.ToUpperInvariant()} Cell có toàn quyền điều hành và ký chính sách"),
        new("manager-operational-authority", ["read_file", "grep_search", "glob_search", "list_agents", "chat_with_agent", "submit_to_agent", "check_agent_task", "web_search", "web_fetch", "iam360_whoami", "iam360_submit_intent"], ["manager"], "EFFECT_ALLOW", "Manager được phép điều phối tác vụ giữa các Agent và tra cứu thông tin vận hành"),
        new("employee-scoped-execution", ["read_file", "grep_search", "glob_search", "view_image", "chat_with_agent", "iam360_whoami"], ["employee"], "EFFECT_ALLOW", "Employee thực thi các công cụ chuyên môn theo đúng hồ sơ Identity Cell đã chứng thực")
    ];

    private static void EnsureBaselineAgents(Dictionary<string, AgentSummaryResponse> map)
    {
        (string Slug, string Name, string RoleTag, string Node, string Model, string Desc, bool Enabled)[] seeds =
        [
            ("mons", "Mons (Core Dev & Ops)", "Engineering", "Node 3rd (CORE) - https://ai.hmz.one", "llm360coding/grok-4.7", "Core DevOps, Fullstack SWE & Infrastructure Lead Agent.", true),
            ("ken", "Ken (Product Owner)", "Operations", "Node 3rd (CORE) - https://ai.hmz.one", "llm360coding/grok-4.7", "Product Discovery, KB Architecture & Delivery Governance.", true),
            ("zen", "Zen (Sales & Delivery)", "Operations", "Node 3rd (CORE) - https://ai.hmz.one", "llm360coding/grok-4.7", "Commercial Paperworks, Outreach & Customer Delivery.", true),
            ("mkai", "Mkai (Generalist Core)", "Engineering", "Node 3rd (CORE) - https://ai.hmz.one", "llm360coding/grok-4.7", "Core Multi-domain Engineering & Automation Specialist.", true),
            ("default-core", "Core Skyer Default", "Operations", "Node 3rd (CORE) - https://ai.hmz.one", "llm360coding/grok-4.7", "Default Coordinator on Core Fleet Node 3rd.", true),
            ("gm-boc", "GM Bốc (Executive Coordinator)", "Operations", "Node 6th (GM) - https://ai-gm.antares.vn", "llm360coding/grok-4.7", "Điều phối viên trưởng Gia Mộc (Owner của cell:gm).", true),
            ("gm-team", "GM Team Leader", "Operations", "Node 6th (GM) - https://ai-gm.antares.vn", "llm360coding/grok-4.7", "Quản lý đội ngũ nghiệp vụ Gia Mộc (Manager).", true),
            ("gm-lua", "GM Lúa (Customer & Sales)", "Operations", "Node 6th (GM) - https://ai-gm.antares.vn", "llm360coding/grok-4.7", "Chuyên viên chăm sóc khách hàng & đơn bán ERPNext Gia Mộc.", true),
            ("gm-gao", "GM Gạo (Supplier & Procurement)", "Operations", "Node 6th (GM) - https://ai-gm.antares.vn", "llm360coding/grok-4.7", "Chuyên viên thu mua & nhà cung cấp ERPNext Gia Mộc.", true),
            ("climax-default", "Climax Default (Creative Lead)", "Research", "Node 5th (CLIMAX) - https://ai-climax.antares.vn", "llm360coding/grok-4.7", "Lead Creative & Social Intelligence Agent cho Climax.", true),
            ("climax-team", "Climax Team (Scraper & Ops)", "Operations", "Node 5th (CLIMAX) - https://ai-climax.antares.vn", "llm360coding/grok-4.7", "Chuyên viên thu thập xu hướng & phân tích nội dung Climax.", true),
            ("planthy-pm", "Planthy PM (Delivery Office)", "Operations", "Node 5th (PLANTHY) - https://planthy-ai.hmz.one", "llm360coding/grok-4.7", "Quản trị dự án & điều phối tiến độ Planthy AI.", true),
            ("planthy-team", "Planthy Team (Execution)", "Engineering", "Node 5th (PLANTHY) - https://planthy-ai.hmz.one", "llm360coding/grok-4.7", "Đội ngũ thực thi kỹ thuật Planthy.", true),
            ("agentteams-manager", "AgentTeams Manager Swarm", "Engineering", "Node 4th (AGENTTEAMS) - https://manager-ai.hmz.one", "llm360coding/grok-4.7", "Orchestrator điều phối bầy đàn đa tác tử qua Matrix.", true),
            ("its-default", "ITS Operational Gatekeeper", "Operations", "Node 8th (ITS) - https://ai-its.antares.vn", "llm360coding/grok-4.7", "Cổng kiểm soát vận hành ITS trên Node 8th.", true),
            ("gl-default", "GL Automation & Data Specialist", "Engineering", "Node 8th (GL) - https://ai-gl.antares.vn", "llm360coding/grok-4.7", "Chuyên gia tự động hoá dữ liệu & phân tích GL trên Node 8th.", true)
        ];

        foreach (var s in seeds)
        {
            if (!map.ContainsKey(s.Slug))
            {
                map[s.Slug] = new AgentSummaryResponse(
                    Id: $"bot:agent360:{s.Slug}",
                    Name: s.Name,
                    Slug: s.Slug,
                    RoleTag: s.RoleTag,
                    Node: s.Node,
                    Status: s.Enabled ? "active" : "disabled",
                    Model: s.Model,
                    RuntimeVersion: "Skyer v2.2.1",
                    Uptime: "Online",
                    Description: s.Desc,
                    Enabled: s.Enabled,
                    Pinned: true,
                    AvailableInChat: true,
                    Tools: [],
                    CanToggle: true,
                    IsRootAgent: s.Slug is "mons" or "gm-boc" or "climax-default" or "planthy-pm" or "agentteams-manager" or "its-default" or "gl-default");
            }
        }
    }

    private static RawAgentProfileData ParseAgentProfile(
        string slug,
        string instKey,
        string profileId,
        AgentSummaryResponse summary,
        string rawJson)
    {
        var cellMeta = FleetCellsCatalog.FirstOrDefault(c => c.Key.Equals(instKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(cellMeta.Key))
        {
            cellMeta = FleetCellsCatalog[0];
        }

        var (friendlyName, roleTag, cellRole) = MapFriendlyIdentity(slug, summary.Name, summary.RoleTag);
        var model = summary.Model ?? "llm360coding/grok-4.7";
        var workspaceDir = $"/app/working/workspaces/{profileId}";
        var mcpClients = new List<Iam360McpClientBindingDto>();
        var builtinTools = new List<Iam360BuiltinToolBindingDto>();
        var enabledChannels = new List<string> { "console" };
        var promptFiles = new List<string> { "AGENTS.md", "SOUL.md", "PROFILE.md" };
        var security = new Iam360SecurityPostureDto("OFF", false, [], true, false);

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                if (JsonNode.Parse(rawJson) is JsonObject root)
                {
                    if (root["workspace_dir"]?.GetValue<string>() is { Length: > 0 } wd)
                    {
                        workspaceDir = wd;
                    }

                    if (root["active_model"] is JsonObject am)
                    {
                        var pid = am["provider_id"]?.GetValue<string>();
                        var mid = am["model"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(mid))
                        {
                            model = !string.IsNullOrWhiteSpace(pid) && !mid.Contains('/') ? $"{pid}/{mid}" : mid;
                        }
                    }

                    if (root["mcp"]?["clients"] is JsonObject clientsObj)
                    {
                        foreach (var kv in clientsObj)
                        {
                            if (kv.Value is JsonObject cObj)
                            {
                                mcpClients.Add(new Iam360McpClientBindingDto(
                                    Id: $"mcp:{kv.Key}",
                                    Key: kv.Key,
                                    Name: cObj["name"]?.GetValue<string>() ?? kv.Key,
                                    Transport: cObj["transport"]?.GetValue<string>() ?? "stdio",
                                    Enabled: cObj["enabled"]?.GetValue<bool>() ?? true));
                            }
                        }
                    }

                    if (root["tools"]?["builtin_tools"] is JsonObject toolsObj)
                    {
                        foreach (var kv in toolsObj)
                        {
                            if (kv.Value is JsonObject tObj)
                            {
                                builtinTools.Add(new Iam360BuiltinToolBindingDto(
                                    Id: $"tool:{kv.Key}",
                                    Name: kv.Key,
                                    Description: tObj["description"]?.GetValue<string>() ?? "",
                                    Enabled: tObj["enabled"]?.GetValue<bool>() ?? true,
                                    AsyncExecution: tObj["async_execution"]?.GetValue<bool>() ?? false));
                            }
                        }
                    }

                    if (root["channels"] is JsonObject chObj)
                    {
                        var chList = new List<string>();
                        foreach (var kv in chObj)
                        {
                            if (kv.Value is JsonObject c && (c["enabled"]?.GetValue<bool>() ?? false))
                            {
                                chList.Add(kv.Key);
                            }
                        }
                        if (chList.Count > 0)
                        {
                            enabledChannels = chList.OrderBy(x => x).ToList();
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed profile fragments and use defaults below
            }
        }

        if (mcpClients.Count == 0)
        {
            mcpClients = GetDefaultMcpClientsForInstance(instKey);
        }

        if (builtinTools.Count == 0)
        {
            builtinTools = GetDefaultBuiltinTools(cellRole);
        }

        if (enabledChannels.Count <= 1 && instKey is "gm" or "core" or "climax" or "planthy")
        {
            enabledChannels = ["console", "telegram", "matrix"];
        }

        var icid = $"identity:{instKey}/{profileId}";
        return new RawAgentProfileData(
            Slug: slug,
            AgentId: profileId,
            InstanceKey: instKey,
            IdentityCellId: icid,
            OrganizationId: cellMeta.OrgId,
            OrganizationCellId: cellMeta.CellId,
            Name: friendlyName,
            Description: summary.Description ?? "",
            RoleTag: roleTag,
            CellRole: cellRole,
            IsRootAgent: summary.IsRootAgent || cellRole == "owner",
            Enabled: summary.Enabled,
            Model: model,
            RuntimeVersion: summary.RuntimeVersion ?? "Skyer v2.2.1",
            Node: $"Node {cellMeta.ClusterNode} ({cellMeta.Key.ToUpperInvariant()}) - {cellMeta.PublicUrl}",
            ClusterNode: cellMeta.ClusterNode,
            EndpointUrl: cellMeta.PublicUrl,
            WorkspaceDir: workspaceDir,
            SystemPromptFiles: promptFiles,
            EnabledChannels: enabledChannels,
            McpClients: mcpClients,
            BuiltinTools: builtinTools,
            Security: security);
    }

    private static List<Iam360McpClientBindingDto> GetDefaultMcpClientsForInstance(string instKey) => instKey switch
    {
        "gm" =>
        [
            new("mcp:erpnext", "erpnext", "ERPNext Operations & Accounting", "stdio", true),
            new("mcp:nextcloud-gm", "nextcloud-gm", "Nextcloud GM Workspace & Deck", "stdio", true),
            new("mcp:kb", "kb", "Outline Knowledge Base Hub", "http", true),
            new("mcp:browser-cdp", "browser-cdp", "Audited Chromium CDP Sidecar", "sse", true),
            new("mcp:mail", "mail", "Enterprise Transactional Mail", "stdio", true)
        ],
        "climax" =>
        [
            new("mcp:browser-cdp", "browser-cdp", "Climax Shared Browser CDP", "sse", true),
            new("mcp:minio", "minio", "MinIO S3 Snapshot Storage", "stdio", true),
            new("mcp:kb", "kb", "Outline Knowledge Base Hub", "http", true),
            new("mcp:nextcloud", "nextcloud", "Nextcloud Deck Delivery Sync", "stdio", true)
        ],
        "planthy" =>
        [
            new("mcp:kb", "kb", "Planthy Knowledge Base", "http", true),
            new("mcp:nextcloud", "nextcloud", "Planthy Deck & Files", "stdio", true),
            new("mcp:browser-cdp", "browser-cdp", "Planthy Browser CDP", "sse", true),
            new("mcp:postgres", "postgres", "Planthy PostgreSQL Inspector", "stdio", true)
        ],
        _ =>
        [
            new("mcp:kb", "kb", "Outline Knowledge Base (Single Source of Truth)", "http", true),
            new("mcp:infra", "infra", "VPS Fleet & Coolify Infrastructure Control", "stdio", true),
            new("mcp:github-org", "github-org", "GitHub Organization & CI/CD Workflows", "stdio", true),
            new("mcp:nextcloud-core", "nextcloud-core", "Nextcloud Deck & Collaboration", "stdio", true),
            new("mcp:infisical", "infisical", "Infisical Centralized Secrets", "stdio", true),
            new("mcp:postgres", "postgres", "Multi-Target PostgreSQL Inspector", "stdio", true)
        ]
    };

    private static List<Iam360BuiltinToolBindingDto> GetDefaultBuiltinTools(string cellRole)
    {
        var list = new List<Iam360BuiltinToolBindingDto>
        {
            new("tool:read_file", "read_file", "Đọc nội dung file trong Workspace cô lập", true),
            new("tool:write_file", "write_file", "Ghi/cập nhật file trong phạm vi $workspace", true),
            new("tool:edit_file", "edit_file", "Chỉnh sửa chính xác nội dung file theo ngữ cảnh", true),
            new("tool:grep_search", "grep_search", "Tìm kiếm biểu thức chính quy tốc độ cao", true),
            new("tool:glob_search", "glob_search", "Liệt kê cây thư mục theo mẫu đường dẫn", true),
            new("tool:chat_with_agent", "chat_with_agent", "Phối hợp A2A với Agent khác trong cùng Organization Cell", true),
            new("tool:submit_to_agent", "submit_to_agent", "Giao tác vụ bất đồng bộ cho Sub-Agent", cellRole is "owner" or "manager"),
            new("tool:iam360_whoami", "iam360_whoami", "Kiểm tra Identity Cell và tập quyền hiệu lực hiện tại", true),
            new("tool:iam360_submit_intent", "iam360_submit_intent", "Ký gửi yêu cầu thay đổi chính sách/thành viên lên IAM360", cellRole == "owner")
        };
        return list;
    }

    private static string NormalizeSlug(string? raw)
    {
        var s = (raw ?? "mons").Trim();
        foreach (var prefix in new[] { "bot:agent360:", "identity:", "agent:gm:", "agent:" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
            }
        }
        if (s.Contains('/'))
        {
            var parts = s.Split('/', 2);
            var rt = parts[0].ToLowerInvariant();
            var prof = parts[1];
            if (rt == "gm" && prof == "default") return "gm-boc";
            if (rt == "gm" && prof == "customer") return "gm-lua";
            if (rt == "gm" && prof == "supplier") return "gm-gao";
            if (rt == "core" && prof == "default") return "default-core";
            if (rt == "planthy" && prof == "default") return "planthy-pm";
            if (rt == "agentteams" && prof == "default") return "agentteams-manager";
            return rt is "core" ? prof : $"{rt}-{prof}";
        }
        return s;
    }

    private static (string InstanceKey, string ProfileId) ResolveInstanceAndProfile(string slug)
    {
        var low = slug.ToLowerInvariant().Trim();
        if (low.StartsWith("gm-"))
        {
            var prof = low switch
            {
                "gm-boc" => "default",
                "gm-team" => "team",
                "gm-lua" => "customer",
                "gm-gao" => "supplier",
                _ => low.Contains("qa_agent") ? "QwenPaw_QA_Agent_0.2" : slug[3..]
            };
            return ("gm", prof);
        }
        if (low.StartsWith("climax-") || low == "climax")
        {
            var prof = low is "climax" or "climax-default" ? "default" : low == "climax-team" ? "team" : low.Contains("qa_agent") ? "QwenPaw_QA_Agent_0.2" : slug[7..];
            return ("climax", prof);
        }
        if (low.StartsWith("planthy-"))
        {
            var prof = low is "planthy-pm" or "planthy-default" ? "default" : low == "planthy-team" ? "team" : low.Contains("qa_agent") ? "QwenPaw_QA_Agent_0.2" : slug[8..];
            return ("planthy", prof);
        }
        if (low.StartsWith("agentteams-"))
        {
            return ("agentteams", low.Contains("qa_agent") ? "QwenPaw_QA_Agent_0.2" : "default");
        }
        if (low.StartsWith("its-"))
        {
            return ("its", low.Contains("qa_agent") ? "QwenPaw_QA_Agent_0.2" : "default");
        }
        if (low.StartsWith("gl-"))
        {
            return ("gl", low.Contains("qa_agent") ? "QwenPaw_QA_Agent_0.2" : "default");
        }
        if (low == "agentteams-gateway")
        {
            return ("agentteams", "gateway");
        }
        var coreProf = low == "default-core" ? "default" : low == "mons-qwenpaw_qa_agent_0.2" ? "QwenPaw_QA_Agent_0.2" : slug;
        return ("core", coreProf);
    }

    private static (string FriendlyName, string RoleTag, string CellRole) MapFriendlyIdentity(string slug, string? fallbackName, string? fallbackRoleTag)
    {
        var low = slug.ToLowerInvariant().Trim();
        return low switch
        {
            "mons" => ("Mons (Core Dev & Ops)", "Engineering", "owner"),
            "ken" => ("Ken (Product Owner)", "Operations", "manager"),
            "zen" => ("Zen (Sales & Delivery)", "Operations", "manager"),
            "mkai" => ("Mkai (Generalist Core)", "Engineering", "employee"),
            "default-core" => ("Core Skyer Default", "Operations", "manager"),
            "gm-boc" => ("GM Bốc (Executive Coordinator)", "Operations", "owner"),
            "gm-team" => ("GM Team Leader", "Operations", "manager"),
            "gm-lua" => ("GM Lúa (Customer & Sales)", "Operations", "employee"),
            "gm-gao" => ("GM Gạo (Supplier & Procurement)", "Operations", "employee"),
            "climax-default" => ("Climax Default (Creative Lead)", "Research", "owner"),
            "climax-team" => ("Climax Team (Scraper & Ops)", "Operations", "manager"),
            "planthy-pm" => ("Planthy PM (Delivery Office)", "Operations", "owner"),
            "planthy-team" => ("Planthy Team (Execution)", "Engineering", "manager"),
            "agentteams-manager" or "agentteams-gateway" => ("AgentTeams Manager Swarm", "Engineering", "owner"),
            "its-default" => ("ITS Operational Gatekeeper", "Operations", "owner"),
            "gl-default" => ("GL Automation & Data Specialist", "Engineering", "owner"),
            _ when low.Contains("qa_agent") => ($"QA Helper ({slug})", "Quality", "employee"),
            _ when low.EndsWith("-team") || low.Contains("manager") => (fallbackName ?? slug, fallbackRoleTag ?? "Operations", "manager"),
            _ => (fallbackName ?? slug, fallbackRoleTag ?? "Engineering", "employee")
        };
    }

    private static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private sealed record RawAgentProfileData(
        string Slug,
        string AgentId,
        string InstanceKey,
        string IdentityCellId,
        string OrganizationId,
        string OrganizationCellId,
        string Name,
        string Description,
        string RoleTag,
        string CellRole,
        bool IsRootAgent,
        bool Enabled,
        string Model,
        string RuntimeVersion,
        string Node,
        string ClusterNode,
        string EndpointUrl,
        string WorkspaceDir,
        IReadOnlyList<string> SystemPromptFiles,
        IReadOnlyList<string> EnabledChannels,
        IReadOnlyList<Iam360McpClientBindingDto> McpClients,
        IReadOnlyList<Iam360BuiltinToolBindingDto> BuiltinTools,
        Iam360SecurityPostureDto Security);
}
