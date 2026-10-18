using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent360.API.Features.Agents.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>Where one agent's Chromium lives and which on-disk profile it owns.</summary>
public sealed record AgentBrowserInfo(
    string AgentId,
    string Instance,
    string Node,
    string Service,
    string ViewerUrl,
    string CdpUrl,
    string Profile,
    string? BrowserContextId,
    bool CdpReachable,
    bool ProfileWindowOpen,
    IReadOnlyList<AgentBrowserTab> Tabs);

public sealed record AgentBrowserTab(string TargetId, string Title, string Url, bool Anchor);

/// <summary>
/// Agent -> browser service/profile directory. Every instance runs one linuxserver/chromium
/// (Selkies) desktop; each agent owns one persistent profile (QWENPAW_AGENT_PROFILE_MAP_B64 on
/// the browser service). The chromium watchdog keeps one anchor tab per profile whose URL names
/// the profile, so a profile's CDP browserContextId is resolved live from Target.getTargets.
/// </summary>
public sealed class AgentBrowserDirectory(
    IFleetInstanceRoutingService routing,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<AgentBrowserDirectory> logger)
{
    internal const string AnchorPrefix = "http://127.0.0.1:3000/agent360-profile?profile=";

    private sealed record BrowserService(string Service, string ViewerUrl, string CdpUrl, IReadOnlyDictionary<string, string> Profiles);

    // Non-secret; mirrors AGENT_PROFILE_MAPS in .agent/skills/core/browser_cdp/scripts/chromium-template.py.
    private static readonly IReadOnlyDictionary<string, BrowserService> Services = new Dictionary<string, BrowserService>(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = new("chromium-browser-q4yu", "https://browser.hmz.one", "http://100.76.185.111:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mons"] = "Default", ["ken"] = "Profile 1", ["zen"] = "Profile 2", ["mkai"] = "Profile 3" }),
        ["gm"] = new("browser-gm-21er", "https://browser-gm.antares.vn", "http://100.81.66.42:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 1", ["customer"] = "Profile 2", ["supplier"] = "Profile 3", ["QwenPaw_QA_Agent_0.2"] = "Profile 4" }),
        ["climax"] = new("climax-browser", "https://browser-climax.antares.vn", "http://100.70.201.83:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 2", ["QwenPaw_QA_Agent_0.2"] = "Profile 3" }),
    };

    public bool TryResolve(string agentId, out ResolvedInstanceTarget target, out string service, out string viewerUrl, out string cdpUrl, out string? profile)
    {
        target = routing.ResolveTarget(agentId);
        service = viewerUrl = cdpUrl = "";
        profile = null;
        if (target.Instance is null || !Services.TryGetValue(target.Instance.Key, out var svc))
        {
            return false;
        }
        service = svc.Service;
        viewerUrl = svc.ViewerUrl;
        cdpUrl = svc.CdpUrl;
        profile = svc.Profiles.TryGetValue(target.TargetAgentProfile, out var p) ? p : null;
        return true;
    }

    public async Task<AgentBrowserInfo?> GetAsync(string agentId, CancellationToken ct)
    {
        if (!TryResolve(agentId, out var target, out var service, out var viewer, out var cdp, out var profile) || profile is null)
        {
            return null;
        }
        var key = $"agent360:browser:{target.Instance!.Key}:{profile}";
        if (cache.TryGetValue(key, out AgentBrowserInfo? cached) && cached is not null)
        {
            return cached with { AgentId = agentId };
        }

        var reachable = false;
        string? ctx = null;
        var tabs = new List<AgentBrowserTab>();
        try
        {
            var all = await ListTargetsAsync(cdp, ct);
            reachable = true;
            ctx = all.FirstOrDefault(t => t.Type == "page" && ProfileOf(t.Url) == profile)?.BrowserContextId;
            if (ctx is not null)
            {
                tabs.AddRange(all.Where(t => t.Type == "page" && t.BrowserContextId == ctx)
                    .Select(t => new AgentBrowserTab(t.TargetId, t.Title, t.Url, ProfileOf(t.Url) is not null)));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Browser CDP for {Agent} at {Cdp} unavailable: {Error}", agentId, cdp, ex.Message);
        }

        var info = new AgentBrowserInfo(agentId, target.Instance.Key, target.ClusterNode, service, viewer, cdp, profile, ctx, reachable, ctx is not null, tabs);
        cache.Set(key, info, TimeSpan.FromSeconds(reachable ? 5 : 15));
        return info;
    }

    /// <summary>The agent's own pages only (never another profile's), most useful first.</summary>
    public async Task<IReadOnlyList<AgentBrowserTab>> AgentPagesAsync(string agentId, CancellationToken ct)
    {
        var info = await GetAsync(agentId, ct);
        if (info is null)
        {
            return [];
        }
        return info.Tabs.OrderBy(t => t.Anchor).ToList();
    }

    internal static string? ProfileOf(string url)
    {
        if (!url.StartsWith(AnchorPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var raw = url[AnchorPrefix.Length..];
        var amp = raw.IndexOf('&');
        return Uri.UnescapeDataString((amp >= 0 ? raw[..amp] : raw).Replace('+', ' '));
    }

    internal sealed record CdpTarget(string TargetId, string Type, string Title, string Url, string? BrowserContextId);

    private async Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(string cdpUrl, CancellationToken ct)
    {
        using var http = httpClientFactory.CreateClient("agent360-browser-cdp");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var version = await http.GetFromJsonAsync<JsonObject>($"{cdpUrl.TrimEnd('/')}/json/version", cts.Token);
        var browserWs = version?["webSocketDebuggerUrl"]?.GetValue<string>()
            ?? throw new InvalidOperationException("CDP did not return a browser websocket");
        // The tailnet proxy rewrites the advertised host; keep the endpoint we can actually reach.
        var wsUri = new UriBuilder(browserWs) { Host = new Uri(cdpUrl).Host, Port = new Uri(cdpUrl).Port }.Uri;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wsUri, cts.Token);
        var req = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Target.getTargets\"}");
        await ws.SendAsync(req, WebSocketMessageType.Text, true, cts.Token);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, cts.Token);
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            using var doc = JsonDocument.Parse(ms.ToArray());
            if (!doc.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != 1)
            {
                continue;
            }
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            return doc.RootElement.GetProperty("result").GetProperty("targetInfos").EnumerateArray()
                .Select(t => new CdpTarget(
                    t.GetProperty("targetId").GetString() ?? "",
                    t.GetProperty("type").GetString() ?? "",
                    t.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    t.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                    t.TryGetProperty("browserContextId", out var c) ? c.GetString() : null))
                .ToList();
        }
    }
}
