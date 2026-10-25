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
        .WithSummary("Get complete IAM360 Two-Cell Strategy (Organization Cells, Identity Cells, and Global Permission Tree)")
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
        .WithSummary("Inspect one agent's Identity Cell, Organization Cell memberships, and hierarchical permission tree")
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

        // 4. POST /api/v1/agents/iam360/organization-cells/{cellId}/members/role
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

        // 5. POST /api/v1/agents/iam360/organization-cells/{cellId}/enforcement
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

        // 6. POST /api/v1/agents/iam360/evaluate
        group.MapPost("evaluate", async (
            [FromBody] Iam360EvaluateAccessRequest request,
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var evaluation = await cellsService.EvaluateAccessAsync(request, ct);
            return TypedResults.Ok(evaluation);
        })
        .WithName("EvaluateIam360CellAccess")
        .WithSummary("Simulate ReBAC + Cerbos ABAC access decision across Identity Cell and Organization Cell")
        .AllowAnonymous();

        // 7. GET /api/v1/agents/iam360/signed-intents
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

        // 8. POST /api/v1/agents/iam360/live-test-all
        group.MapPost("live-test-all", async (
            IIam360CellsService cellsService,
            CancellationToken ct) =>
        {
            var report = await cellsService.RunLiveTestAllAgentsAsync(ct);
            return TypedResults.Ok(report);
        })
        .WithName("RunIam360LiveTestAllAgents")
        .WithSummary("Execute live Two-Cell inspection, permission tree verification, and ReBAC/ABAC/Baseline evaluation across all current fleet agents")
        .AllowAnonymous();
    }
}
