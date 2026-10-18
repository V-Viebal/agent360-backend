using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Agent360.API.Features.Agents.Browser;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class AgentBrowserFocusLiveTests
{
    [Fact]
    public async Task CanFocusAndRaiseWindowOnCoreBrowser()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(AgentBrowserDirectory.HttpClientName);
        var sp = services.BuildServiceProvider();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        // Test with core browser on tailnet: 100.76.185.111:19222
        var cdpUrl = "http://100.76.185.111:19222";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // 1. Focus Profile 2 (Zen): target 0C041666AC4C554A59A3324D6C4386A5, context 85637262DE873305746D8914DD940C14
        var method = typeof(AgentBrowserCdpEndpoint).GetMethod("ActivateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();

        var task1 = (Task<bool>)method!.Invoke(null, new object?[] { httpFactory, cdpUrl, "0C041666AC4C554A59A3324D6C4386A5", "85637262DE873305746D8914DD940C14", cts.Token })!;
        var ok1 = await task1;
        ok1.Should().BeTrue();

        // 2. Switch & Focus Profile 1 (Ken): target 9F358775D4BB3FB6B1C36491B1955183, context 8BBFCB1525273155B8D67003D8AD398D
        var task2 = (Task<bool>)method.Invoke(null, new object?[] { httpFactory, cdpUrl, "9F358775D4BB3FB6B1C36491B1955183", "8BBFCB1525273155B8D67003D8AD398D", cts.Token })!;
        var ok2 = await task2;
        ok2.Should().BeTrue();
    }
}
