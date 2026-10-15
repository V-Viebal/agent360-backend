using Agent360.API.Features.Agents.Security;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class IamRevocationGuardTests
{
    [Fact]
    public void IamRevocationGuard_HaltsExecution_WhenRevoked()
    {
        const string identityCell = "cell:org-test:mons";
        const string botId = "bot:agent360:mons";

        Assert.True(IamRevocationGuard.IsExecutionAllowed(botId, out _));

        IamRevocationGuard.MarkIdentityCellRevoked(identityCell, "Security incident detected");

        var allowed = IamRevocationGuard.IsExecutionAllowed(botId, out var reason);
        Assert.False(allowed);
        Assert.NotNull(reason);
        Assert.Contains("Security incident detected", reason);

        // Restore
        IamRevocationGuard.MarkIdentityCellActive(identityCell);
        Assert.True(IamRevocationGuard.IsExecutionAllowed(botId, out _));
    }
}
