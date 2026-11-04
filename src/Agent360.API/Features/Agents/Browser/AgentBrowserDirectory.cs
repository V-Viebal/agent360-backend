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
    private readonly ConcurrentDictionary<string, long> _recentlyClosedTargets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (AgentBrowserTab Tab, long Tick)> _recentlyCreatedTargets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (ClientWebSocket Socket, SemaphoreSlim Lock)> _browserSockets = new(StringComparer.OrdinalIgnoreCase);

    private sealed record BrowserService(string Service, string ViewerUrl, string CdpUrl, IReadOnlyDictionary<string, string> Profiles);

    // Non-secret; mirrors AGENT_PROFILE_MAPS in .agent/skills/core/browser_cdp/scripts/chromium-template.py.
    private static readonly IReadOnlyDictionary<string, BrowserService> Services = new Dictionary<string, BrowserService>(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = new("chromium-browser-q4yu", "https://browser.hmz.one", "http://100.76.185.111:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mons"] = "Default", ["default"] = "Profile 4", ["ken"] = "Profile 1", ["zen"] = "Profile 2", ["mkai"] = "Profile 3", ["QwenPaw_QA_Agent_0.2"] = "Profile 5", ["AgentCore_QA_Agent_0.2"] = "Profile 5", ["app360"] = "Profile 6", ["mons-app360"] = "Profile 6" }),
        ["gm"] = new("browser-gm-21er", "https://browser-gm.antares.vn", "http://100.81.66.42:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 1", ["customer"] = "Profile 2", ["supplier"] = "Profile 3", ["QwenPaw_QA_Agent_0.2"] = "Profile 4", ["AgentCore_QA_Agent_0.2"] = "Profile 4" }),
        // Planthy runs its own browser service (not the shared 5th climax browser); tailnet proxy on :19224.
        ["planthy"] = new("planthy-browser-rff7", "https://browser-planthy.hmz.one", "http://100.70.201.83:19224",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["default"] = "Default",
                ["team"] = "Profile 1",
                ["Planthy_Dev_Agent_0.1"] = "Profile 2",
                ["QwenPaw_QA_Agent_0.2"] = "Profile 3",
                ["AgentCore_QA_Agent_0.2"] = "Profile 3",
                ["marketing_scout"] = "Profile 4",
                ["marketing_creator"] = "Profile 5",
                ["marketing_publisher"] = "Profile 6"
            }),
        ["climax"] = new("climax-browser", "https://browser-climax.antares.vn", "http://100.70.201.83:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["team"] = "Profile 2", ["QwenPaw_QA_Agent_0.2"] = "Profile 3", ["AgentCore_QA_Agent_0.2"] = "Profile 3" }),
        ["its"] = new("browser-its", "https://browser-its.antares.vn", "http://100.66.243.55:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["QwenPaw_QA_Agent_0.2"] = "Profile 1", ["AgentCore_QA_Agent_0.2"] = "Profile 1" }),
        ["gl"] = new("browser-gl-2o45ucsrp9rp9yepsrnvs1mk", "https://browser-gl.antares.vn", "http://100.66.243.55:19224",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["QwenPaw_QA_Agent_0.2"] = "Profile 1", ["AgentCore_QA_Agent_0.2"] = "Profile 1" }),
        ["qwenpaw-test"] = new("browser-test-tg733z0rtlfvn6oir46nf8ca", "https://browser-test.antares.vn", "http://100.99.201.47:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["agentcore-ba"] = "Default", ["agentcore-acc"] = "Profile 1", ["QwenPaw_QA_Agent_0.2"] = "Profile 2", ["AgentCore_QA_Agent_0.2"] = "Profile 2", ["agentcore-catalog"] = "Default", ["agentcore-commerce"] = "Default", ["agentcore-import"] = "Default", ["agentcore-mgr"] = "Default", ["agentcore-product-dev"] = "Default" }),
        ["agentcore-test"] = new("browser-test-tg733z0rtlfvn6oir46nf8ca", "https://browser-test.antares.vn", "http://100.99.201.47:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["agentcore-ba"] = "Default", ["agentcore-acc"] = "Profile 1", ["QwenPaw_QA_Agent_0.2"] = "Profile 2", ["AgentCore_QA_Agent_0.2"] = "Profile 2", ["agentcore-catalog"] = "Default", ["agentcore-commerce"] = "Default", ["agentcore-import"] = "Default", ["agentcore-mgr"] = "Default", ["agentcore-product-dev"] = "Default" }),
        ["monosbase"] = new("browser-gm-21er", "https://browser-gm.antares.vn", "http://100.81.66.42:19222",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = "Default", ["agentcore-mgr"] = "Default", ["agentcore-acc"] = "Profile 1", ["QwenPaw_QA_Agent_0.2"] = "Profile 4", ["AgentCore_QA_Agent_0.2"] = "Profile 4" }),
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
        // Built-in profiles contain the canonical agent mappings for all known profiles (including planthy marketing agents).
        // If an env override is missing a newly added profile, merge it with built-in profiles so no agent gets a 404.
        var envProfiles = ParseProfileMap(cfg?.BrowserProfileMap);
        IReadOnlyDictionary<string, string>? profiles;
        if (envProfiles is not null && builtIn?.Profiles is not null)
        {
            var merged = new Dictionary<string, string>(builtIn.Profiles, StringComparer.OrdinalIgnoreCase);
            foreach (var (profKey, profVal) in envProfiles) merged[profKey] = profVal;
            profiles = merged;
        }
        else
        {
            profiles = envProfiles ?? builtIn?.Profiles;
        }
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

    public sealed record DynamicMcpBrowserResolution(
        bool HasBrowserMcp,
        string? InstanceKey,
        string? Service,
        string? ViewerUrl,
        string? CdpUrl,
        string? Profile);

    public async Task<(bool Ok, ResolvedInstanceTarget Target, string Service, string ViewerUrl, string CdpUrl, string? Profile)> ResolveDynamicAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var hasService = TryResolve(agentId, out var target, out var service, out var viewerUrl, out var cdpUrl, out var profile);
        if (!hasService || target.Instance is null)
        {
            return (false, target, service, viewerUrl, cdpUrl, profile);
        }

        var slug = FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant();
        var cacheKey = $"agent360:browser-mcp-bind:{slug}";
        if (!cache.TryGetValue(cacheKey, out DynamicMcpBrowserResolution? mcpRes))
        {
            mcpRes = await ProbeAgentBrowserMcpAsync(target, ct);
            cache.Set(cacheKey, mcpRes, TimeSpan.FromSeconds(mcpRes is not null ? 45 : 20));
        }

        if (mcpRes is not null)
        {
            if (!mcpRes.HasBrowserMcp)
            {
                return (false, target, "", "", "", null);
            }
            if (!string.IsNullOrWhiteSpace(mcpRes.Service)) service = mcpRes.Service!;
            if (!string.IsNullOrWhiteSpace(mcpRes.ViewerUrl)) viewerUrl = mcpRes.ViewerUrl!;
            var effectiveKey = !string.IsNullOrWhiteSpace(mcpRes.InstanceKey) ? mcpRes.InstanceKey! : target.Instance.Key;
            if (!string.IsNullOrWhiteSpace(mcpRes.CdpUrl))
            {
                cdpUrl = InternalCdpUrl(effectiveKey, mcpRes.CdpUrl!);
            }
            if (!string.IsNullOrWhiteSpace(mcpRes.Profile)) profile = mcpRes.Profile!;
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            return (false, target, service, viewerUrl, cdpUrl, null);
        }

        return (true, target, service, viewerUrl, cdpUrl, profile);
    }

    private async Task<DynamicMcpBrowserResolution?> ProbeAgentBrowserMcpAsync(ResolvedInstanceTarget target, CancellationToken ct)
    {
        if (target.Instance is null || string.IsNullOrWhiteSpace(target.BaseUrl))
        {
            return null;
        }
        try
        {
            using var resp = await routing.SendAsync(
                target.Instance,
                HttpMethod.Get,
                $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}",
                agentProfileHeader: target.TargetAgentProfile,
                timeout: TimeSpan.FromSeconds(3),
                ct: ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return ResolveFromAgentProfileJson(json, target.Instance.Key, target.TargetAgentProfile);
        }
        catch
        {
            return null;
        }
    }

    public static DynamicMcpBrowserResolution? ResolveFromAgentProfileJson(string? profileJson, string defaultInstanceKey, string defaultAgentProfile)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }
        try
        {
            var root = JsonNode.Parse(profileJson) as JsonObject;
            var clients = root?["mcp"]?["clients"] as JsonObject;
            if (clients is null)
            {
                return null;
            }

            foreach (var kv in clients)
            {
                if (kv.Value is not JsonObject clientObj) continue;
                var enabled = clientObj["enabled"]?.GetValue<bool>() ?? false;
                if (!enabled) continue;

                var key = kv.Key;
                var name = clientObj["name"]?.ToString() ?? "";
                var cmd = clientObj["command"]?.ToString() ?? "";
                var args = (clientObj["args"] as JsonArray)?.Select(a => a?.ToString() ?? "").ToList() ?? [];
                var env = clientObj["env"] as JsonObject;

                var cdpUrlRaw = env?["BROWSER_CDP_URL"]?.ToString()
                    ?? env?["CHROME_DEVTOOLS_BROWSER_URL"]?.ToString();
                var bindingFile = env?["BROWSER_CDP_BINDING_FILE"]?.ToString();
                var explicitProfile = env?["BROWSER_PROFILE_NAME"]?.ToString();

                var isBrowserMcp = !string.IsNullOrWhiteSpace(cdpUrlRaw)
                    || !string.IsNullOrWhiteSpace(bindingFile)
                    || key.Contains("browser", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("devtools", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("playwright", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("browser", StringComparison.OrdinalIgnoreCase)
                    || cmd.Contains("browser", StringComparison.OrdinalIgnoreCase)
                    || args.Any(a => a.Contains("browser-cdp", StringComparison.OrdinalIgnoreCase) || a.Contains("chrome-devtools", StringComparison.OrdinalIgnoreCase));

                if (!isBrowserMcp) continue;

                var matchedKey = MatchBrowserInstanceKey(cdpUrlRaw, bindingFile, key, name) ?? defaultInstanceKey;
                Services.TryGetValue(matchedKey, out var matchedSvc);
                Services.TryGetValue(defaultInstanceKey, out var defaultSvc);
                var svc = matchedSvc ?? defaultSvc;

                string? resolvedProfile = null;
                if (!string.IsNullOrWhiteSpace(explicitProfile))
                {
                    resolvedProfile = explicitProfile.Equals("default", StringComparison.OrdinalIgnoreCase) ? "Default" : explicitProfile.Trim();
                }
                else if (svc is not null && svc.Profiles.TryGetValue(defaultAgentProfile, out var p))
                {
                    resolvedProfile = p;
                }

                return new DynamicMcpBrowserResolution(
                    HasBrowserMcp: true,
                    InstanceKey: matchedKey,
                    Service: svc?.Service,
                    ViewerUrl: svc?.ViewerUrl,
                    CdpUrl: svc?.CdpUrl,
                    Profile: resolvedProfile);
            }

            // If the Zalo channel is enabled on this agent, allow resolving the built-in browser service/profile
            // so the user can sign into Zalo Web (https://chat.zalo.me) even before a browser MCP client is bound.
            var zaloEnabled = root?["channels"]?["zalo"]?["enabled"]?.GetValue<bool>() ?? false;
            if (zaloEnabled && Services.TryGetValue(defaultInstanceKey, out var zaloSvc) &&
                zaloSvc.Profiles.TryGetValue(defaultAgentProfile, out var zaloProfile))
            {
                return new DynamicMcpBrowserResolution(
                    HasBrowserMcp: true,
                    InstanceKey: defaultInstanceKey,
                    Service: zaloSvc.Service,
                    ViewerUrl: zaloSvc.ViewerUrl,
                    CdpUrl: zaloSvc.CdpUrl,
                    Profile: zaloProfile);
            }

            // Agent profile loaded cleanly and has no enabled browser MCP client -> fail closed.
            return new DynamicMcpBrowserResolution(false, null, null, null, null, null);
        }
        catch
        {
            return null;
        }
    }

    internal static string? MatchBrowserInstanceKey(string? cdpUrl, string? bindingFile, string? clientKey, string? clientName)
    {
        var combined = $"{cdpUrl} {bindingFile} {clientKey} {clientName}".ToLowerInvariant();
        if (combined.Contains("browser-its") || combined.Contains("100.66.243.55:19222") || combined.Contains("/its-")) return "its";
        if (combined.Contains("browser-gl") || combined.Contains("100.66.243.55:19224") || combined.Contains("/gl-")) return "gl";
        if (combined.Contains("planthy-browser") || combined.Contains("100.70.201.83:19224") || combined.Contains("/planthy-")) return "planthy";
        if (combined.Contains("climax-browser") || combined.Contains("100.70.201.83:19222") || combined.Contains("/climax-")) return "climax";
        if (combined.Contains("browser-gm") || combined.Contains("100.81.66.42:19222") || combined.Contains("gm-browser-binding") || combined.Contains("gm-runtime-binding")) return "gm";
        if (combined.Contains("chromium-browser-q4yu") || combined.Contains("100.76.185.111:19222") || combined.Contains("100.76.185.111:19223") || combined.Contains("/core-")) return "core";
        if (combined.Contains("browser-test") || combined.Contains("100.99.201.47:19222") || combined.Contains("/test-")) return "agentcore-test";
        return null;
    }

    public async Task InvalidateCacheAsync(string agentId, CancellationToken ct = default)
    {
        var (ok, _, service, _, _, profile) = await ResolveDynamicAsync(agentId, ct);
        if (ok && profile is not null)
        {
            cache.Remove($"agent360:browser:{service}:{profile}");
        }
    }

    public void RecordTabCreated(string targetId, AgentBrowserTab tab)
    {
        if (string.IsNullOrEmpty(targetId)) return;
        _recentlyClosedTargets.TryRemove(targetId, out _);
        _recentlyCreatedTargets[targetId] = (tab, Environment.TickCount64);
    }

    public void RecordTabClosed(string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return;
        _recentlyCreatedTargets.TryRemove(targetId, out _);
        _recentlyClosedTargets[targetId] = Environment.TickCount64;
    }

    public async Task<IReadOnlyList<AgentBrowserTab>?> MutateCachedTabsAsync(
        string agentId,
        Func<List<AgentBrowserTab>, List<AgentBrowserTab>> mutator,
        CancellationToken ct = default)
    {
        var (ok, _, service, _, _, profile) = await ResolveDynamicAsync(agentId, ct);
        if (!ok || profile is null) return null;
        var key = $"agent360:browser:{service}:{profile}";
        if (cache.TryGetValue(key, out AgentBrowserInfo? cached) && cached is not null)
        {
            var nextTabs = mutator(cached.Tabs.ToList());
            var updated = cached with { Tabs = nextTabs };
            cache.Set(key, updated, TimeSpan.FromSeconds(12));
            return updated.Tabs;
        }
        return null;
    }

    public async Task<AgentBrowserInfo?> GetAsync(string agentId, CancellationToken ct, bool forceRefresh = false)
    {
        var (ok, target, service, viewer, cdp, profile) = await ResolveDynamicAsync(agentId, ct);
        if (!ok || profile is null)
        {
            return null;
        }
        var key = $"agent360:browser:{service}:{profile}";
        if (forceRefresh)
        {
            cache.Remove(key);
        }
        else if (cache.TryGetValue(key, out AgentBrowserInfo? cached) && cached is not null)
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

            // Auto-open anchor tab ONLY for the Default profile if missing.
            // Never call PUT /json/new for non-Default profiles (Profile 1..6) because CDP /json/new
            // has no --profile-directory parameter and injects the anchor tab into the Default/last-active
            // browserContextId, polluting profile context resolution! Non-Default profile windows are
            // opened exclusively by chromium-profile-agent.py with --profile-directory=<profile>.
            if (ctx is null && string.Equals(profile, "Default", StringComparison.OrdinalIgnoreCase))
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
                var nowTick = Environment.TickCount64;
                foreach (var (cid, cTick) in _recentlyClosedTargets)
                {
                    if (nowTick - cTick > 10000) _recentlyClosedTargets.TryRemove(cid, out _);
                }
                foreach (var (nid, entry) in _recentlyCreatedTargets)
                {
                    if (nowTick - entry.Tick > 6000) _recentlyCreatedTargets.TryRemove(nid, out _);
                }

                tabs.AddRange(all.Where(t =>
                        t.Type == "page" &&
                        t.BrowserContextId == ctx &&
                        !_recentlyClosedTargets.ContainsKey(t.TargetId) &&
                        AgentBrowserCdpEndpoint.IsCapturable(t.Url))
                    .Select(t => new AgentBrowserTab(
                        t.TargetId,
                        string.IsNullOrWhiteSpace(t.Title) && _recentlyCreatedTargets.TryGetValue(t.TargetId, out var pend) ? pend.Tab.Title : t.Title,
                        (string.IsNullOrWhiteSpace(t.Url) || t.Url == "about:blank") && _recentlyCreatedTargets.TryGetValue(t.TargetId, out var pendUrl) ? pendUrl.Tab.Url : t.Url,
                        ProfileOf(t.Url) is not null)));

                foreach (var (nid, entry) in _recentlyCreatedTargets)
                {
                    if (nowTick - entry.Tick <= 6000 && !_recentlyClosedTargets.ContainsKey(nid) && tabs.All(x => x.TargetId != nid))
                    {
                        tabs.Add(entry.Tab);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Browser CDP for {Agent} at {Cdp} unavailable: {Error}", agentId, cdp, ex.Message);
        }

        var info = new AgentBrowserInfo(agentId, target.Instance!.Key, target.ClusterNode, service, viewer, cdp, profile, ctx, reachable, ctx is not null, tabs);
        // An unreachable browser is negatively cached longer: a dead node is not re-dialled on every poll.
        cache.Set(key, info, TimeSpan.FromSeconds(reachable ? 12 : 15));
        return info;
    }

    // profile key -> last browserContextId seen with that profile's anchor tab. Agents navigate or close the anchor
    // while working; the context stays theirs as long as any page of it is open, so the browser watchdog no longer
    // has to push a fresh anchor tab in front of the agent's work.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> KnownContexts = new();

    internal static string? ResolveContext(string key, string profile, IReadOnlyCollection<CdpTarget> all)
    {
        var defaultAnchoredCtx = all.FirstOrDefault(t =>
            t.Type == "page" && string.Equals(ProfileOf(t.Url), "Default", StringComparison.OrdinalIgnoreCase))?.BrowserContextId;
        var anchored = all.FirstOrDefault(t =>
            t.Type == "page"
            && string.Equals(ProfileOf(t.Url), profile, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(profile, "Default", StringComparison.OrdinalIgnoreCase)
                || defaultAnchoredCtx is null
                || !string.Equals(t.BrowserContextId, defaultAnchoredCtx, StringComparison.Ordinal)))?.BrowserContextId;
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

        if (KnownContexts.TryGetValue(key, out var known) && all.Any(t => t.Type == "page" && t.BrowserContextId == known))
        {
            return known;
        }
        // Cold-start recovery for the Default profile when its anchor tab was navigated away and no other context is unanchored
        if (string.Equals(profile, "Default", StringComparison.OrdinalIgnoreCase))
        {
            var anchoredContexts = all
                .Where(t => t.Type == "page" && ProfileOf(t.Url) is not null && !string.IsNullOrEmpty(t.BrowserContextId))
                .Select(t => t.BrowserContextId!)
                .ToHashSet(StringComparer.Ordinal);
            var unanchoredContexts = all
                .Where(t => t.Type == "page" && !string.IsNullOrEmpty(t.BrowserContextId) && !anchoredContexts.Contains(t.BrowserContextId!))
                .Select(t => t.BrowserContextId!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (unanchoredContexts.Count == 1)
            {
                KnownContexts[key] = unanchoredContexts[0];
                return unanchoredContexts[0];
            }
        }
        return null;
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var entry = _browserSockets.GetOrAdd(cdpUrl, _ => (new ClientWebSocket(), new SemaphoreSlim(1, 1)));
        await entry.Lock.WaitAsync(cts.Token);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var ws = entry.Socket;
                if (ws.State != WebSocketState.Open)
                {
                    try { ws.Dispose(); } catch { }
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    var http = httpClientFactory.CreateClient(HttpClientName);
                    var version = await http.GetFromJsonAsync<JsonObject>($"{cdpUrl.TrimEnd('/')}/json/version", cts.Token);
                    var browserWs = version?["webSocketDebuggerUrl"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("CDP did not return a browser websocket");
                    var wsUri = new UriBuilder(browserWs) { Host = new Uri(cdpUrl).Host, Port = new Uri(cdpUrl).Port }.Uri;
                    await ws.ConnectAsync(wsUri, cts.Token);
                    entry = (ws, entry.Lock);
                    _browserSockets[cdpUrl] = entry;
                }

                try
                {
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
                            if (r.MessageType == WebSocketMessageType.Close)
                            {
                                throw new WebSocketException("Browser CDP socket closed");
                            }
                            ms.Write(buffer, 0, r.Count);
                        } while (!r.EndOfMessage);
                        using var doc = JsonDocument.Parse(ms.ToArray());
                        if (!doc.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != 1)
                        {
                            continue;
                        }
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
                catch when (attempt == 0)
                {
                    try { ws.Dispose(); } catch { }
                    entry = (new ClientWebSocket(), entry.Lock);
                    _browserSockets[cdpUrl] = entry;
                }
            }
            return [];
        }
        finally
        {
            entry.Lock.Release();
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
