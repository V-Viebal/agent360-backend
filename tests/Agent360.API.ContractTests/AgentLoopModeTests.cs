using Agent360.API.Features.Agents.Endpoints;
using Agent360.API.Features.Agents.SubAgents;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class AgentLoopModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a360-loop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private AgentLoopModeStore Store() => new(MsOptions.Create(new SubAgentOptions { StoragePath = _dir }), new TestEnv(_dir));

    [Fact]
    public async Task LoopMode_DefaultsThenPersistsPerAgentAcrossRestarts()
    {
        var s = Store();
        s.Get("mons")["mode"]!.GetValue<string>().Should().Be("default");
        await s.SetAsync("bot:agent360:mons", "mission", null);
        await s.SetAsync("ken", "goal", null);

        var reopened = Store();
        reopened.Get("MONS")["mode"]!.GetValue<string>().Should().Be("mission", "ids are normalized (bot:agent360: prefix, case)");
        reopened.Get("ken")["mode"]!.GetValue<string>().Should().Be("goal");
        reopened.Get("zen")["mode"]!.GetValue<string>().Should().Be("default");
    }
}
