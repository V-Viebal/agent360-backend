using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agent360.API.Features.Agents.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>A fake Chromium page socket that counts screencast sessions and pushes frames on demand.</summary>
public sealed class ScreencastHubTests : IAsyncLifetime
{
    private WebApplication _cdp = null!;
    private WebApplication _api = null!;
    private Uri _cdpBase = null!;
    private Uri _apiBase = null!;
    private int _cdpSessions;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var c = WebApplication.CreateBuilder();
        c.WebHost.UseUrls("http://127.0.0.1:0");
        _cdp = c.Build();
        _cdp.UseWebSockets();
        _cdp.Map("/devtools/page/{id}", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buf = new byte[8192];
            var r = await ws.ReceiveAsync(buf, ctx.RequestAborted); // Page.startScreencast
            Encoding.UTF8.GetString(buf, 0, r.Count).Should().Contain("Page.startScreencast");
            Interlocked.Increment(ref _cdpSessions);
            for (var i = 0; ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested; i++)
            {
                var frame = JsonSerializer.SerializeToUtf8Bytes(new { method = "Page.screencastFrame", @params = new { data = Convert.ToBase64String([(byte)i]), sessionId = i } });
                try
                {
                    await ws.SendAsync(frame, WebSocketMessageType.Text, true, ctx.RequestAborted);
                    await Task.Delay(50, ctx.RequestAborted);
                }
                catch { break; }
            }
        });
        await _cdp.StartAsync(Ct);
        _cdpBase = new Uri(_cdp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

        var a = WebApplication.CreateBuilder();
        a.WebHost.UseUrls("http://127.0.0.1:0");
        a.Services.AddSingleton(new ScreencastHub(NullLogger<ScreencastHub>.Instance));
        _api = a.Build();
        _api.UseWebSockets();
        _api.Map("/watch", async (HttpContext ctx, ScreencastHub hub) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.WatchAsync(_cdpBase.ToString().TrimEnd('/'), "T1", 640, 55, ws, null, ctx.RequestAborted);
        });
        await _api.StartAsync(Ct);
        _apiBase = new Uri(_api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync();
        await _cdp.StopAsync();
        await _api.DisposeAsync();
        await _cdp.DisposeAsync();
    }

    private async Task<ClientWebSocket> ViewerAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://{_apiBase.Authority}/watch"), Ct);
        return ws;
    }

    private static async Task<string> NextFrameAsync(ClientWebSocket ws)
    {
        var buf = new byte[8192];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return Encoding.UTF8.GetString(buf, 0, r.Count);
    }

    [Fact]
    public async Task ManyViewersShareOneCdpScreencast()
    {
        var viewers = new List<ClientWebSocket>();
        for (var i = 0; i < 8; i++) viewers.Add(await ViewerAsync());
        foreach (var v in viewers) (await NextFrameAsync(v)).Should().Contain("Page.screencastFrame");
        _cdpSessions.Should().Be(1);
        var hub = _api.Services.GetRequiredService<ScreencastHub>();
        hub.ViewerCount(_cdpBase.ToString().TrimEnd('/'), "T1", 640, 55).Should().Be(8);
        foreach (var v in viewers) v.Dispose();
    }

    [Fact]
    public async Task AStalledViewerDoesNotBlockOthers()
    {
        using var stalled = await ViewerAsync(); // never reads
        using var active = await ViewerAsync();
        for (var i = 0; i < 20; i++) (await NextFrameAsync(active)).Should().Contain("screencastFrame");
        _cdpSessions.Should().Be(1);
    }
}
