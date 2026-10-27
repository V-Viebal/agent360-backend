using System.Security.Claims;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Identity;
using Agent360.UserManagement.Domain.Constants;
using Agent360.UserManagement.Infrastructure.Authorization;
using AwesomeAssertions;
using Hmz.Core.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Per-agent routes accept every Admin-policy credential (operator token included), while strictly enforcing DB ownership for regular/demoted members.</summary>
public sealed class AgentOwnershipGuardTests
{
    private static (HttpContext Ctx, CurrentUser User, PermissionSnapshotAccessor Snapshot) Build(
        bool adminPolicyPasses,
        bool isAuthenticated = false,
        long? resolvedUserId = null,
        string? operatorToken = null,
        string? presentedOperatorHeader = null,
        string[]? jwtRoles = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireAssertion(_ => adminPolicyPasses)));
        var snapshot = new PermissionSnapshotAccessor();
        services.AddSingleton<IPermissionSnapshotAccessor>(snapshot);
        if (!string.IsNullOrEmpty(operatorToken))
        {
            services.AddSingleton<IOptionsMonitor<Agent360SecurityOptions>>(
                new StaticOptionsMonitor<Agent360SecurityOptions>(new Agent360SecurityOptions { OperatorToken = operatorToken }));
        }

        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (isAuthenticated)
        {
            var claims = new List<Claim> { new("sub", "user-sub-1") };
            if (jwtRoles is not null)
            {
                foreach (var r in jwtRoles) claims.Add(new Claim("roles", r));
            }
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        }
        if (!string.IsNullOrEmpty(presentedOperatorHeader))
        {
            ctx.Request.Headers[OperatorAuthFilter.HeaderName] = presentedOperatorHeader;
        }

        var user = new CurrentUser(new HttpContextAccessor { HttpContext = ctx });
        if (resolvedUserId.HasValue)
        {
            user.SetResolvedUserId(resolvedUserId.Value);
        }
        return (ctx, user, snapshot);
    }

    private static readonly IConfiguration NoDb = new ConfigurationBuilder().Build();

    [Fact]
    public async Task AdminPolicy_GrantsAccessWithoutLocalProfile()
    {
        var (ctx, user, _) = Build(adminPolicyPasses: true);
        var (allowed, _) = await AgentOwnershipGuard.CheckAccessAsync("bot:agent360:mons", ctx, user, NoDb, TestContext.Current.CancellationToken);
        allowed.Should().BeTrue("the operator token / AdminSubjects satisfy the Admin policy");
    }

    [Fact]
    public async Task NoAdminAndNoProfile_IsDenied()
    {
        var (ctx, user, _) = Build(adminPolicyPasses: false);
        var (allowed, reason) = await AgentOwnershipGuard.CheckAccessAsync("mons", ctx, user, NoDb, TestContext.Current.CancellationToken);
        allowed.Should().BeFalse();
        reason.Should().Contain("not authenticated");
    }

    [Fact]
    public async Task DemotedOrRegularMember_WithLeftoverOperatorHeaderOrStaleJwt_IsStrictlyDeniedWhenNotAssigned()
    {
        // Even if the browser sends X-Agent360-Operator-Token or a stale JWT role="admin",
        // once a user is authenticated and resolved in local DB (UserId = 42) without Users.ManageRoles permission,
        // IsEffectiveAdminAsync must return false and CheckAccessAsync must deny unassigned agents!
        var (ctx, user, snapshot) = Build(
            adminPolicyPasses: true,
            isAuthenticated: true,
            resolvedUserId: 42,
            operatorToken: "s3cret",
            presentedOperatorHeader: "s3cret",
            jwtRoles: ["admin"]);

        snapshot.Set(new PermissionSnapshot(42, new HashSet<string> { UserManagementPermissions.Users.Read }, DateTimeOffset.UtcNow, new Dictionary<long, DateTimeOffset>()));

        var isEffectiveAdmin = await AgentOwnershipGuard.IsEffectiveAdminAsync(ctx, user, NoDb, TestContext.Current.CancellationToken);
        isEffectiveAdmin.Should().BeFalse("PostgreSQL RBAC is authoritative for resolved DB users; leftover operator headers or stale JWT roles must not elevate a member");

        var (allowed, reason) = await AgentOwnershipGuard.CheckAccessAsync("mons", ctx, user, NoDb, TestContext.Current.CancellationToken);
        allowed.Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResolvedDbAdmin_WithManageRolesPermission_IsGrantedFullAccess()
    {
        var (ctx, user, snapshot) = Build(
            adminPolicyPasses: false,
            isAuthenticated: true,
            resolvedUserId: 10);

        snapshot.Set(new PermissionSnapshot(10, new HashSet<string> { UserManagementPermissions.Users.ManageRoles }, DateTimeOffset.UtcNow, new Dictionary<long, DateTimeOffset>()));

        var isEffectiveAdmin = await AgentOwnershipGuard.IsEffectiveAdminAsync(ctx, user, NoDb, TestContext.Current.CancellationToken);
        isEffectiveAdmin.Should().BeTrue();

        var (allowed, _) = await AgentOwnershipGuard.CheckAccessAsync("mons", ctx, user, NoDb, TestContext.Current.CancellationToken);
        allowed.Should().BeTrue();
    }
}
