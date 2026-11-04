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
    private ScreencastHub _hub = null!;
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
        _hub = new ScreencastHub(NullLogger<ScreencastHub>.Instance);
        a.Services.AddSingleton(_hub);
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
        _hub.RetireTarget(_cdpBase.ToString().TrimEnd('/'), "T1");
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

    [Fact]
    public void ReadJpegDimensionsParsesSof0FrameSize()
    {
        // Minimal valid JPEG header with APP0 (0xE0) followed by SOF0 (0xC0) encoding width=1200 (0x04B0), height=650 (0x028A)
        byte[] sampleJpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x8A, 0x04, 0xB0, 0x03,
            0xFF, 0xD9
        ];
        var (w, h) = ScreencastHub.ReadJpegDimensions(sampleJpeg);
        w.Should().Be(1200);
        h.Should().Be(650);
    }

    [Fact]
    public void NormalizeViewportFitsWithinNativeSurfaceWithoutTiling()
    {
        // Desktop FullHD logical viewport: 1920x1080 stays 1920x1080 while ComputeBackingScale maps it inside the 1408x771 surface without tiling
        var (fhdW, fhdH) = ScreencastHub.NormalizeViewport(1920, 1080, 1408, 771, isMobileDevice: false);
        fhdW.Should().Be(1920);
        fhdH.Should().Be(1080);
        var fhdScale = ScreencastHub.ComputeBackingScale(fhdW, fhdH, 1408, 771, isMobileDevice: false);
        ((int)Math.Round(fhdW * fhdScale)).Should().BeLessThanOrEqualTo(1408);
        ((int)Math.Round(fhdH * fhdScale)).Should().BeLessThanOrEqualTo(771);

        // When frontend requests 1312x960 (aspect 1.3667), Desktop FullHD logical viewport scales proportionally to 1476x1080
        var (w1, h1) = ScreencastHub.NormalizeViewport(1312, 960, 1440, 813, isMobileDevice: false);
        w1.Should().Be(1476);
        h1.Should().Be(1080);
        Math.Abs(((double)w1 / h1) - (1312.0 / 960.0)).Should().BeLessThan(0.005);
        var s1 = ScreencastHub.ComputeBackingScale(w1, h1, 1440, 813, isMobileDevice: false);
        ((int)Math.Round(w1 * s1)).Should().Be(1111);
        ((int)Math.Round(h1 * s1)).Should().Be(813);

        // Mobile device viewport (390x660) preserves genuine mobile width (< 640) at 1:1 scale
        var (mw1, mh1) = ScreencastHub.NormalizeViewport(390, 660, 1408, 771, isMobileDevice: true);
        mw1.Should().Be(390);
        mh1.Should().Be(660);
        ScreencastHub.ComputeBackingScale(mw1, mh1, 1408, 771, isMobileDevice: true).Should().Be(1.0);

        // Tall mobile viewport (390x844) clamps height to native surface (771) and preserves aspect ratio (356x771)
        var (mw2, mh2) = ScreencastHub.NormalizeViewport(390, 844, 1408, 771, isMobileDevice: true);
        mw2.Should().Be(356);
        mh2.Should().Be(771);
    }
}
