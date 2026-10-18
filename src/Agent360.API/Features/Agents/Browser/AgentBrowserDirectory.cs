using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Serialization;
using Agent360.API.Features.Agents.Services;
using Agent360.API.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Browser;

/// <summary>Where one agent's Chromium lives and which on-disk profile it owns.</summary>
public sealed record AgentBrowserInfo(
    string AgentId,
    string Instance,
    string Node,
    string Service,
    string ViewerUrl,
    // Internal tailnet CDP endpoint and context id are server-side only: never serialized to clients.
    [property: JsonIgnore] string CdpUrl,
    string Profile,
    [property: JsonIgnore] string? BrowserContextId,
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
    ILogger<AgentBrowserDirectory> logger,
    IOptions<QwenPawOptions>? options = null,
    AgentBrowserGate? gate = null)
{
    internal const string AnchorPrefix = "data:text/html;charset=utf-8,<!--agent360-profile=";
    internal const string LegacyAnchorPrefix = "http://127.0.0.1:3000/agent360-profile?profile=";
    public const string HttpClientName = "agent360-browser-cdp";
    private readonly QwenPawOptions _options = options?.Value ?? new QwenPawOptions();
    private readonly AgentBrowserGate _gate = gate ?? new AgentBrowserGate();

    private sealed record BrowserService(string Service, string ViewerUrl, string CdpUrl, IReadOnlyDictionary<string, string> Profiles);

    // Non-secret; mirrors AGENT_PROFILE_MAPS in .agent/skills/core/browser_cdp/scripts/chromium-template.py.
    private static readonly IReadOnlyDictionary<string, BrowserService> Services = new Dictionary<string, BrowserService>(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = new("chromium-browser-q4yu", "https://browser.hmz.one", "http://100.76.185.111:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mons"] = "Default", ["ken"] = "Profile 1", ["zen"] = "Profile 2", ["mkai"] = "Profile 3" }),
        ["gm"] = new("browser-gm-21er", "https://browser-gm.antares.vn", "http://100.81.66.42:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 1", ["customer"] = "Profile 2", ["supplier"] = "Profile 3", ["QwenPaw_QA_Agent_0.2"] = "Profile 4" }),
        // Planthy runs its own browser service (not the shared 5th climax browser); tailnet proxy on :19224.
        ["planthy"] = new("planthy-browser-rff7", "https://browser-planthy.hmz.one", "http://100.70.201.83:19224",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 1", ["Planthy_Dev_Agent_0.1"] = "Profile 2", ["QwenPaw_QA_Agent_0.2"] = "Profile 3" }),
        ["climax"] = new("climax-browser", "https://browser-climax.antares.vn", "http://100.70.201.83:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 2", ["QwenPaw_QA_Agent_0.2"] = "Profile 3" }),
        // Planthy has its own browser on 5th, exposed on the tailnet at :19224 (climax owns :19222 there).
        ["planthy"] = new("planthy-browser-rff7", "https://browser-planthy.hmz.one", "http://100.70.201.83:19224",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 1", ["Planthy_Dev_Agent_0.1"] = "Profile 2", ["QwenPaw_QA_Agent_0.2"] = "Profile 3" }),
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
        cdpUrl = InternalCdpUrl(target.Instance.Key, svc.CdpUrl);
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
        // Single-flight per browser profile: concurrent viewers/polls share one CDP probe.
        var info = await _gate.Coalesce(key, () => LoadAsync(agentId, key, target, service, viewer, cdp, profile));
        return info with { AgentId = agentId };
    }

    private async Task<AgentBrowserInfo> LoadAsync(string agentId, string key, ResolvedInstanceTarget target, string service, string viewer, string cdp, string profile)
    {
        // Detached from the first caller's token so a cancelled request cannot fail the shared probe.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var ct = cts.Token;
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
        catch (Exception ex)
        {
            logger.LogWarning("Browser CDP for {Agent} at {Cdp} unavailable: {Error}", agentId, cdp, ex.Message);
        }

        var info = new AgentBrowserInfo(agentId, target.Instance!.Key, target.ClusterNode, service, viewer, cdp, profile, ctx, reachable, ctx is not null, tabs);
        // An unreachable browser is negatively cached longer: a dead node is not re-dialled on every poll.
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

    /// <summary>Configured override (QwenPaw__Instances__&lt;key&gt;__BrowserCdpUrl) is accepted only when it is an
    /// internal endpoint; the browser is always reached over the tailnet/private network, never a public route.</summary>
    private string InternalCdpUrl(string instanceKey, string fallback)
    {
        if (_options.Instances.TryGetValue(instanceKey, out var cfg) && !string.IsNullOrWhiteSpace(cfg.BrowserCdpUrl))
        {
            var url = cfg.BrowserCdpUrl.Trim().TrimEnd('/');
            if (IsInternalEndpoint(url))
            {
                return url;
            }
            logger.LogWarning("Ignoring non-internal browser CDP override for instance {Instance}", instanceKey);
        }
        return fallback;
    }

    /// <summary>http(s) to a private/CGNAT (tailnet) IP or a single-label/.internal service name.</summary>
    internal static bool IsInternalEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(u.UserInfo))
        {
            return false;
        }
        if (IPAddress.TryParse(u.Host, out var ip))
        {
            var b = ip.GetAddressBytes();
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (
                b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127));
        }
        return !u.Host.Contains('.') || u.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ProfileOf(string url)
    {
        if (url.StartsWith(AnchorPrefix, StringComparison.Ordinal))
        {
            var rest = url[AnchorPrefix.Length..];
            var end = rest.IndexOf("-->", StringComparison.Ordinal);
            return end > 0 ? Uri.UnescapeDataString(rest[..end]) : null;
        }
        if (url.StartsWith(LegacyAnchorPrefix, StringComparison.Ordinal))
        {
            var raw = url[LegacyAnchorPrefix.Length..];
            var amp = raw.IndexOf('&');
            return Uri.UnescapeDataString((amp >= 0 ? raw[..amp] : raw).Replace('+', ' '));
        }
        return null;
    }

    internal sealed record CdpTarget(string TargetId, string Type, string Title, string Url, string? BrowserContextId);

    private async Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(string cdpUrl, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
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

/// <summary>Process-wide browser coordination: single-flight probes/screenshots and a per-instance cap on
/// concurrent screencasts so viewers cannot exhaust a node's Chromium.</summary>
public sealed class AgentBrowserGate
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflight = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _streams = new();

    public int MaxStreamsPerInstance { get; init; } = 6;

    public async Task<T> Coalesce<T>(string key, Func<Task<T>> factory)
    {
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await factory()));
        try
        {
            return (T)(await lazy.Value)!;
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, lazy));
        }
    }

    public SemaphoreSlim StreamSlots(string instance) =>
        _streams.GetOrAdd(instance, _ => new SemaphoreSlim(MaxStreamsPerInstance, MaxStreamsPerInstance));
}
