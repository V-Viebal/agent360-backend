using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Security;

public sealed class Iam360CellsEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("agents/iam360")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags(EndpointTags.Agents);

        // 1. GET /api/v1/agents/iam360/cells-strategy
        group.MapGet("cells-strategy", async (
            [FromQuery] bool? force,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var result = await cellsService.GetCellsStrategyAsync(force == true, ct);
            return TypedResults.Ok(result);
        })
        .WithName("GetIam360CellsStrategy")
        .WithSummary("Get complete IAM360 Two-Cell Strategy (Organization Cells, Identity Cells, Approvals, Reconciliation, and 5-Branch Global Permission Tree)")
        .AllowAnonymous();

        // 2. GET /api/v1/agents/{agentId}/iam360-cell
        routeBuilder.MapGet("agents/{agentId}/iam360-cell", async (
            string agentId,
            [FromQuery] bool? force,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var inspection = await cellsService.GetAgentCellInspectionAsync(agentId, force == true, ct);
            return inspection is not null
                ? Results.Ok(inspection)
                : Results.NotFound(new { message = $"Identity Cell for agent '{agentId}' not found." });
        })
        .MapToApiVersion(ApiVersion.Default)
        .WithTags(EndpointTags.Agents)
        .WithName("GetAgentIam360CellInspection")
        .WithSummary("Inspect one agent's Identity Cell, Organization Cell memberships, A2A Delegation Targets, Observed Principals, and 5-Branch Permission Tree")
        .AllowAnonymous();

        // 3. POST /api/v1/agents/iam360/identity-cells/{identityId}/status
        group.MapPost("identity-cells/{identityId}/status", async (
            string identityId,
            [FromBody] Iam360IdentityStatusMutationRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var updated = await cellsService.MutateIdentityStatusAsync(
                Uri.UnescapeDataString(identityId),
                request.Action,
                request.Reason ?? "Operator lifecycle update",
                ct);

            return updated is not null
                ? Results.Ok(updated)
                : Results.NotFound(new { message = $"Identity cell '{identityId}' not found." });
        })
        .WithName("MutateIam360IdentityCellStatus")
        .WithSummary("Activate, suspend, or revoke an Agent Identity Cell with persistent signed intent")
        .AllowAnonymous();

        // 4. POST /api/v1/agents/iam360/identity-cells/{identityId}/capabilities/toggle (Upgrade #1)
        group.MapPost("identity-cells/{identityId}/capabilities/toggle", async (
            string identityId,
            [FromBody] Iam360CapabilityToggleRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var updated = await cellsService.MutateCapabilityToggleAsync(
                Uri.UnescapeDataString(identityId),
                request.CapabilityKind,
                request.CapabilityKey,
                request.Enabled,
                request.Reason ?? "Inline tree capability toggle",
                ct);

            return updated is not null
                ? Results.Ok(updated)
                : Results.NotFound(new { message = $"Identity cell '{identityId}' not found." });
        })
        .WithName("ToggleIam360IdentityCapability")
        .WithSummary("Inline toggle an MCP client or Builtin Tool on the permission tree and emit a signed intent")
        .AllowAnonymous();

        // 5. POST /api/v1/agents/iam360/identity-cells/{identityId}/reconcile (Upgrade #1)
        group.MapPost("identity-cells/{identityId}/reconcile", async (
            string identityId,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var updated = await cellsService.ReconcileIdentityDriftAsync(
                Uri.UnescapeDataString(identityId),
                "Operator 1-click runtime drift reconciliation",
                ct);

            return updated is not null
                ? Results.Ok(updated)
                : Results.NotFound(new { message = $"Identity cell '{identityId}' not found." });
        })
        .WithName("ReconcileIam360IdentityDrift")
        .WithSummary("Reconcile Desired vs Observed capability drift for an Agent Identity Cell")
        .AllowAnonymous();

        // 6. POST /api/v1/agents/iam360/organization-cells/{cellId}/members/role
        group.MapPost("organization-cells/{cellId}/members/role", async (
            string cellId,
            [FromBody] Iam360OrgRoleMutationRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var updated = await cellsService.MutateOrgMemberRoleAsync(
                Uri.UnescapeDataString(cellId),
                request.IdentityCellId,
                request.Role,
                request.Reason ?? "Operator role assignment update",
                ct);

            return updated is not null
                ? Results.Ok(updated)
                : Results.NotFound(new { message = $"Organization cell '{cellId}' not found." });
        })
        .WithName("MutateIam360OrgMemberRole")
        .WithSummary("Update an Agent Identity Cell's role (owner, manager, employee) inside an Organization Cell with persistent signed intent")
        .AllowAnonymous();

        // 7. POST /api/v1/agents/iam360/organization-cells/{cellId}/enforcement
        group.MapPost("organization-cells/{cellId}/enforcement", async (
            string cellId,
            [FromBody] Iam360OrgEnforcementMutationRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var updated = await cellsService.MutateOrgEnforcementAsync(
                Uri.UnescapeDataString(cellId),
                request.RuntimeEnforcement,
                request.Reason ?? "Operator enforcement mode update",
                ct);

            return updated is not null
                ? Results.Ok(updated)
                : Results.NotFound(new { message = $"Organization cell '{cellId}' not found." });
        })
        .WithName("MutateIam360OrgEnforcement")
        .WithSummary("Set runtime enforcement mode (off, preview, on) for an Organization Cell with persistent signed intent")
        .AllowAnonymous();

        // 8. POST /api/v1/agents/iam360/organization-cells/{cellId}/transfer-owner (Upgrade #5)
        group.MapPost("organization-cells/{cellId}/transfer-owner", async (
            string cellId,
            [FromBody] Iam360OwnerTransferRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var approval = await cellsService.RequestOwnerTransferAsync(
                Uri.UnescapeDataString(cellId),
                request.NewOwnerIdentityCellId,
                request.RequestedBy ?? "user:platform:admin-requester",
                request.Reason ?? "Two-person owner transfer request",
                ct);

            return TypedResults.Ok(approval);
        })
        .WithName("RequestIam360OwnerTransfer")
        .WithSummary("Request an Organization Cell Owner Identity Cell transfer subject to Two-Person Rule approval")
        .AllowAnonymous();

        // 9. GET /api/v1/agents/iam360/approvals (Upgrade #2 & #5)
        group.MapGet("approvals", (IIam360CellsService cellsService) =>
        {
            var items = cellsService.GetApprovals();
            return TypedResults.Ok(new
            {
                schema = "iam360/two-person-approvals/v1",
                count = items.Count,
                pending_count = items.Count(a => a.Status == "pending"),
                items
            });
        })
        .WithName("GetIam360TwoPersonApprovals")
        .WithSummary("List pending and decided Two-Person Rule & Step-Up Approvals")
        .AllowAnonymous();

        // 10. POST /api/v1/agents/iam360/approvals/{approvalId}/decide (Upgrade #2 & #5)
        group.MapPost("approvals/{approvalId}/decide", async (
            string approvalId,
            [FromBody] Iam360ApprovalDecisionRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var decided = await cellsService.DecideApprovalAsync(
                Uri.UnescapeDataString(approvalId),
                request.Approve,
                request.DecidedBy ?? "user:platform:second-approver",
                request.Reason ?? "Two-person security review completed",
                ct);

            return decided is not null
                ? Results.Ok(decided)
                : Results.NotFound(new { message = $"Approval '{approvalId}' not found." });
        })
        .WithName("DecideIam360Approval")
        .WithSummary("Approve or reject a pending Two-Person Rule or Step-Up Action approval")
        .AllowAnonymous();

        // 11. GET /api/v1/agents/iam360/reconciliation-status (Upgrade #6)
        group.MapGet("reconciliation-status", (IIam360CellsService cellsService) =>
        {
            return TypedResults.Ok(cellsService.GetReconciliationStatus());
        })
        .WithName("GetIam360ReconciliationStatus")
        .WithSummary("Get real-time 15-minute background reconciliation status and drift counter")
        .AllowAnonymous();

        // 12. POST /api/v1/agents/iam360/evaluate
        group.MapPost("evaluate", async (
            [FromBody] Iam360EvaluateAccessRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var evaluation = await cellsService.EvaluateAccessAsync(request, ct);
            return TypedResults.Ok(evaluation);
        })
        .WithName("EvaluateIam360CellAccess")
        .WithSummary("Simulate ReBAC + Cerbos ABAC + Step-Up + A2A access decision across Identity Cell and Organization Cell")
        .AllowAnonymous();

        // 13. GET /api/v1/agents/iam360/signed-intents
        group.MapGet("signed-intents", (IIam360CellsService cellsService) =>
        {
            var ledger = cellsService.GetSignedIntentsLedger();
            return TypedResults.Ok(new
            {
                schema = "iam360/signed-intents-ledger/v1",
                count = ledger.Count,
                items = ledger
            });
        })
        .WithName("GetIam360SignedIntentsLedger")
        .WithSummary("Get persistent signed IAM360 intent envelopes emitted by Agent360")
        .AllowAnonymous();

        // 14. POST /api/v1/agents/iam360/live-test-all
        group.MapPost("live-test-all", async (
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var report = await cellsService.RunLiveTestAllAgentsAsync(ct);
            return TypedResults.Ok(report);
        })
        .WithName("RunIam360LiveTestAllAgents")
        .WithSummary("Execute live Two-Cell inspection, 5-branch permission tree verification, A2A boundary check, and ReBAC/ABAC/Baseline evaluation across all current fleet agents")
        .AllowAnonymous();
    }
}
