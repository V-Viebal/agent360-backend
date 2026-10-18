using Agent360.API.Features.Agents.Security;
using Agent360.API.Identity;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Per-agent routes accept every Admin-policy credential (operator token included), not only Logto roles.</summary>
public sealed class AgentOwnershipGuardTests
{
    private static (HttpContext Ctx, CurrentUser User) Build(bool adminPolicyPasses)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireAssertion(_ => adminPolicyPasses)));
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        return (ctx, new CurrentUser(new HttpContextAccessor { HttpContext = ctx }));
    }

    private static readonly IConfiguration NoDb = new ConfigurationBuilder().Build();

    [Fact]
    public async Task AdminPolicy_GrantsAccessWithoutLocalProfile()
    {
        var (ctx, user) = Build(adminPolicyPasses: true);
        var (allowed, _) = await AgentOwnershipGuard.CheckAccessAsync("bot:agent360:mons", ctx, user, NoDb, TestContext.Current.CancellationToken);
        allowed.Should().BeTrue("the operator token / AdminSubjects satisfy the Admin policy");
    }

    [Fact]
    public async Task NoAdminAndNoProfile_IsDenied()
    {
        var (ctx, user) = Build(adminPolicyPasses: false);
        var (allowed, reason) = await AgentOwnershipGuard.CheckAccessAsync("mons", ctx, user, NoDb, TestContext.Current.CancellationToken);
        allowed.Should().BeFalse();
        reason.Should().Contain("not authenticated");
    }
}
