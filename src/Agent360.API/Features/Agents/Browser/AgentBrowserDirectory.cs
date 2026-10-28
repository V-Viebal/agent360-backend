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
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mons"] = "Default", ["default"] = "Default", ["ken"] = "Profile 1", ["zen"] = "Profile 2", ["mkai"] = "Profile 3" }),
        ["gm"] = new("browser-gm-21er", "https://browser-gm.antares.vn", "http://100.81.66.42:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 1", ["customer"] = "Profile 2", ["supplier"] = "Profile 3", ["QwenPaw_QA_Agent_0.2"] = "Profile 4" }),
        // Planthy runs its own browser service (not the shared 5th climax browser); tailnet proxy on :19224.
        ["planthy"] = new("planthy-browser-rff7", "https://browser-planthy.hmz.one", "http://100.70.201.83:19224",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 1", ["Planthy_Dev_Agent_0.1"] = "Profile 2", ["QwenPaw_QA_Agent_0.2"] = "Profile 3" }),
        ["climax"] = new("climax-browser", "https://browser-climax.antares.vn", "http://100.70.201.83:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 2", ["QwenPaw_QA_Agent_0.2"] = "Profile 3" }),
        ["its"] = new("browser-gl-2o45ucsrp9rp9yepsrnvs1mk", "https://browser-gl.antares.vn", "http://100.66.243.55:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["QwenPaw_QA_Agent_0.2"] = "Profile 1" }),
        ["gl"] = new("browser-gl-2o45ucsrp9rp9yepsrnvs1mk", "https://browser-gl.antares.vn", "http://100.66.243.55:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["QwenPaw_QA_Agent_0.2"] = "Profile 1" }),
        ["qwenpaw-test"] = new("browser-test-tg733z0rtlfvn6oir46nf8ca", "https://browser-test.antares.vn", "http://100.99.201.47:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["agentcore-acc"] = "Profile 1", ["QwenPaw_QA_Agent_0.2"] = "Profile 2" }),
    };

    public bool TryResolve(string agentId, out ResolvedInstanceTarget target, out string service, out string viewerUrl, out string cdpUrl, out string? profile)
    {
        target = routing.ResolveTarget(agentId);
        service = viewerUrl = cdpUrl = "";
        profile = null;
        if (target.Instance is null || ResolveService(target.Instance.Key) is not { } svc)
        {
            return false;
        }
        service = svc.Service;
        viewerUrl = svc.ViewerUrl;
        cdpUrl = InternalCdpUrl(target.Instance.Key, svc.CdpUrl);
        profile = svc.Profiles.TryGetValue(target.TargetAgentProfile, out var p) ? p : null;
        return true;
    }

    /// <summary>Built-in browser map, overridden field by field by QwenPaw:Instances:{key}:Browser*.</summary>
    private BrowserService? ResolveService(string key)
    {
        Services.TryGetValue(key, out var builtIn);
        _options.Instances.TryGetValue(key, out var cfg);
        var service = Nonblank(cfg?.BrowserService) ?? builtIn?.Service;
        var viewer = Nonblank(cfg?.BrowserViewerUrl) is { } v && v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? v.TrimEnd('/') : builtIn?.ViewerUrl;
        var profiles = ParseProfileMap(cfg?.BrowserProfileMap) ?? builtIn?.Profiles;
        if (service is null || viewer is null || profiles is null)
        {
            return null;
        }
        return new BrowserService(service, viewer, builtIn?.CdpUrl ?? "", profiles);
    }

    private static string? Nonblank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static IReadOnlyDictionary<string, string>? ParseProfileMap(string? raw)
    {
        raw = Nonblank(raw);
        if (raw is null)
        {
            return null;
        }
        try
        {
            if (!raw.StartsWith('{'))
            {
                raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            }
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (map is null || map.Count == 0)
            {
                return null;
            }
            return new Dictionary<string, string>(map.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
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
            ctx = ResolveContext(key, profile, all);

            // Auto-open anchor tab if missing for the requested profile so window is never lost
            if (ctx is null && !string.IsNullOrWhiteSpace(profile))
            {
                try
                {
                    var http = httpClientFactory.CreateClient(HttpClientName);
                    var newAnchorUrl = BuildAnchorUrl(profile, target.TargetAgentProfile);
                    using var putReq = new HttpRequestMessage(HttpMethod.Put, $"{cdp.TrimEnd('/')}/json/new?{Uri.EscapeDataString(newAnchorUrl)}");
                    using var putRes = await http.SendAsync(putReq, ct);
                    if (putRes.IsSuccessStatusCode)
                    {
                        all = await ListTargetsAsync(cdp, ct);
                        ctx = ResolveContext(key, profile, all);
                    }
                }
                catch { }
            }
            else if (ctx is not null)
            {
                // Upgrade any legacy bodyless anchor tab (which renders 100% blank white #FFFFFF) to the styled dark-theme anchor
                var bodylessAnchor = all.FirstOrDefault(t =>
                    t.Type == "page"
                    && t.BrowserContextId == ctx
                    && ProfileOf(t.Url) == profile
                    && !t.Url.Contains("<body", StringComparison.OrdinalIgnoreCase));
                if (bodylessAnchor is not null)
                {
                    try
                    {
                        await NavigateTargetAsync(cdp, bodylessAnchor.TargetId, BuildAnchorUrl(profile, target.TargetAgentProfile), ct);
                    }
                    catch { }
                }
            }

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

    // profile key -> last browserContextId seen with that profile's anchor tab. Agents navigate or close the anchor
    // while working; the context stays theirs as long as any page of it is open, so the browser watchdog no longer
    // has to push a fresh anchor tab in front of the agent's work.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> KnownContexts = new();

    internal static string? ResolveContext(string key, string profile, IReadOnlyCollection<CdpTarget> all)
    {
        var anchored = all.FirstOrDefault(t => t.Type == "page" && ProfileOf(t.Url) == profile)?.BrowserContextId;
        if (anchored is not null)
        {
            KnownContexts[key] = anchored;
            return anchored;
        }

        // For the 'Default' profile: if no explicit anchor tab exists, find the page target with null or empty contextId
        if (string.Equals(profile, "Default", StringComparison.OrdinalIgnoreCase))
        {
            var defaultPage = all.FirstOrDefault(t => t.Type == "page" && string.IsNullOrEmpty(t.BrowserContextId));
            if (defaultPage is not null)
            {
                return ""; // Represents the default unisolated browser context
            }
        }

        return KnownContexts.TryGetValue(key, out var known) && all.Any(t => t.Type == "page" && t.BrowserContextId == known)
            ? known
            : null;
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

    public static string BuildAnchorUrl(string profile, string agentName)
    {
        var q = Uri.EscapeDataString(profile);
        var display = Uri.EscapeDataString(string.IsNullOrWhiteSpace(agentName) ? profile : agentName);
        return $"{AnchorPrefix}{q}--><title>Agent profile: {display}</title>"
            + "<body style='margin:0;height:100vh;display:flex;flex-direction:column;gap:8px;align-items:center;justify-content:center;"
            + "background:%230d0d12;color:%23cfcfd8;font:16px system-ui,sans-serif'>"
            + "<div>Agent360 %C2%B7 Browser Profile <b style='color:%23ff7a00'>&nbsp;" + display + "</b></div>"
            + "<div style='font-size:12px;color:%2371717a;font-family:monospace'>Ready for autonomous navigation (" + q + ")</div></body>";
    }

    private static async Task NavigateTargetAsync(string cdpUrl, string targetId, string url, CancellationToken ct)
    {
        var cdp = new Uri(cdpUrl);
        var pageWs = new Uri($"ws://{cdp.Authority}/devtools/page/{Uri.EscapeDataString(targetId)}");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(pageWs, ct);
        var msg = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method = "Page.navigate", @params = new { url } });
        await ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
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

    /// <summary>Live viewers per instance. Viewers of one tab share a CDP screencast, so this only bounds sockets.</summary>
    public int MaxStreamsPerInstance { get; init; } = 50;

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
