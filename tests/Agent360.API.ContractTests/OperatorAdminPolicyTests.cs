using Agent360.API.Features.Agents.SubAgents;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>The operator token is accepted as an admin credential only when one is configured and presented exactly.</summary>
public sealed class OperatorAdminPolicyTests
{
    private static HttpContext Ctx(string? header = null, string? bearer = null)
    {
        var c = new DefaultHttpContext();
        if (header is not null) c.Request.Headers[OperatorAuthFilter.HeaderName] = header;
        if (bearer is not null) c.Request.Headers.Authorization = $"Bearer {bearer}";
        return c;
    }

    [Fact]
    public void PresentsOperatorToken_RequiresConfiguredExactMatch()
    {
        var opt = new Agent360SecurityOptions { OperatorToken = "op-secret", RequireOperatorForWrites = false };
        OperatorAuthFilter.PresentsOperatorToken(Ctx(header: "op-secret"), opt).Should().BeTrue();
        OperatorAuthFilter.PresentsOperatorToken(Ctx(bearer: "op-secret"), opt).Should().BeTrue();
        OperatorAuthFilter.PresentsOperatorToken(Ctx(header: "wrong"), opt).Should().BeFalse();
        OperatorAuthFilter.PresentsOperatorToken(Ctx(), opt).Should().BeFalse();
        // RequireOperatorForWrites=false must not turn into "everyone is admin"
        OperatorAuthFilter.PresentsOperatorToken(Ctx(), new Agent360SecurityOptions { OperatorToken = "" }).Should().BeFalse();
        OperatorAuthFilter.PresentsOperatorToken(Ctx(header: ""), new Agent360SecurityOptions { OperatorToken = "" }).Should().BeFalse();
    }
}
