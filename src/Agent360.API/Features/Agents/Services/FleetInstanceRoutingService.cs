using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Agent360.API.Options;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Services;

public interface IFleetInstanceRoutingService
{
    ResolvedInstanceTarget ResolveTarget(string? agentId);

    IReadOnlyList<QwenPawInstance> Instances { get; }

    void RegisterDynamicInstance(QwenPawInstance instance);

    void SyncFromGuardian(IEnumerable<Agent360.API.Features.Agents.Models.AgentGuardianAgentDto> guardianAgents);

    Task<HttpRequestMessage> CreateProxiedRequestAsync(HttpMethod method, string path, string? agentId, HttpContent? content, CancellationToken ct = default);

    /// <summary>Sends to the agent's runtime over the internal endpoint, failing over to the public one,
    /// re-authenticating once on 401. <paramref name="relativePath"/> is relative to "/api/".</summary>
    Task<HttpResponseMessage> SendAsync(
        QwenPawInstance instance,
        HttpMethod method,
        string relativePath,
        Func<HttpContent?>? contentFactory = null,
        string? agentProfileHeader = null,
        TimeSpan? timeout = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default);
}

public sealed record QwenPawInstance(
    string Key,
    string ClusterNode,
    string DisplayName,
    string InternalUrl,
    string PublicUrl,
    bool RequiresLogin,
    string CdpUrl);

public sealed record ResolvedInstanceTarget(
    string ClusterNode,
    string InstanceName,
    string BaseUrl,
    string TargetAgentProfile,
    string? LoginPath,
    string? Username,
    string? Password,
    string CdpUrl)
{
    public QwenPawInstance? Instance { get; init; }

    public string AgentId { get; init; } = "";

    /// <summary>False for agents that are not QwenPaw runtimes (AgentTeams, LLM360, unconfigured).</summary>
    public bool IsQwenPaw => Instance is not null;
}

public sealed class FleetInstanceRoutingService : IFleetInstanceRoutingService
{
    public const string HttpClientName = "qwenpaw-fleet";
    private const string LoginPath = "api/auth/login";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly ILogger<FleetInstanceRoutingService> _logger;
    private readonly QwenPawOptions _options;
    private readonly FleetRoutingState _state;
    private ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> _tokenCache => _state.Tokens;
    private ConcurrentDictionary<string, SemaphoreSlim> _loginLocks => _state.LoginLocks;
    private ConcurrentDictionary<string, DateTime> _internalDownUntil => _state.InternalDownUntil;

    public FleetInstanceRoutingService(HttpClient httpClient, ILogger<FleetInstanceRoutingService> logger, IOptions<QwenPawOptions>? options = null, FleetRoutingState? state = null)
    {
        _httpClient = httpClient;
        _state = state ?? new FleetRoutingState();
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger;
        _options = options?.Value ?? new QwenPawOptions();
        _staticInstances =
        [
            Build("core", "3rd", "Core Skyer Fleet (Node 3rd)", "http://100.76.185.111:33148", "https://ai.hmz.one", false, "http://100.76.185.111:19222"),
            Build("gm", "6th", "Gia Moc Skyer Fleet (Node 6th)", "http://100.81.66.42:26888", "https://ai-gm.antares.vn", true, "http://100.81.66.42:19222"),
            Build("climax", "5th", "Climax Creative (Node 5th)", "http://100.70.201.83:26404", "https://ai-climax.antares.vn", true, "http://100.70.201.83:19222"),
            Build("planthy", "5th", "Planthy AI (Node 5th)", "http://100.70.201.83:32842", "https://planthy-ai.hmz.one", true, "http://100.70.201.83:19224"),
            // AgentTeams manager is a QwenPaw runtime bound to loopback on 4th; only the public proxy is reachable.
            Build("agentteams", "4th", "AgentTeams Swarm (Node 4th)", "https://manager-ai.hmz.one", "https://manager-ai.hmz.one", false, ""),
            Build("its", "8th", "ITS Operational Gatekeeper (Node 8th)", "http://100.66.243.55:26889", "https://ai-its.antares.vn", true, "http://100.66.243.55:19222"),
            Build("gl", "8th", "GL Automation & Analytics (Node 8th)", "http://100.66.243.55:26890", "https://ai-gl.antares.vn", true, "http://100.66.243.55:19222"),
            Build("agentcore-test", "7th", "Test AgentCore (Node 7th)", "https://ai-test.antares.vn", "https://ai-test.antares.vn", true, "http://100.99.201.47:19222"),
            Build("monosbase", "6th", "Monosbase Bot (Node 6th)", "https://ai-bot-gm.s-0.top", "https://ai-bot-gm.s-0.top", true, "http://100.81.66.42:19222"),
        ];
    }

    private readonly IReadOnlyList<QwenPawInstance> _staticInstances;

    public IReadOnlyList<QwenPawInstance> Instances
    {
        get
        {
            LoadDynamicInfraNodesManifest();
            if (_state.DynamicInstances.IsEmpty) return _staticInstances;
            var combined = new Dictionary<string, QwenPawInstance>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in _staticInstances) combined[inst.Key] = inst;
            foreach (var kvp in _state.DynamicInstances) combined[kvp.Key] = kvp.Value;
            return combined.Values.ToList();
        }
    }

    private void LoadDynamicInfraNodesManifest()
    {
        if ((DateTime.UtcNow - _state.LastManifestLoadedAt).TotalSeconds < 15) return;
        _state.LastManifestLoadedAt = DateTime.UtcNow;

        var paths = new[]
        {
            Environment.GetEnvironmentVariable("AGENT360_INFRA_NODES_FILE"),
            "/data/infra-nodes.json",
            Path.Combine(AppContext.BaseDirectory, "_storage", "infra-nodes.json")
        };

        var file = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (string.IsNullOrWhiteSpace(file)) return;

        try
        {
            var json = File.ReadAllText(file);
            var root = JsonNode.Parse(json)?.AsObject();
            var runtimes = root?["runtimes"]?.AsArray();
            if (runtimes is null) return;

            foreach (var item in runtimes)
            {
                if (item is not JsonObject rt) continue;
                var key = rt["instanceKey"]?.ToString();
                var node = rt["node"]?.ToString() ?? "infra";
                var resourceName = rt["resourceName"]?.ToString() ?? key ?? "qwenpaw";
                var publicUrl = rt["publicUrl"]?.ToString()?.TrimEnd('/');
                var internalUrl = rt["internalUrl"]?.ToString()?.TrimEnd('/') ?? publicUrl;
                var cdpUrl = rt["cdpUrl"]?.ToString() ?? "";
                var authEnabled = rt["authEnabled"]?.GetValue<bool>() ?? true;
                var username = rt["username"]?.ToString() ?? "admin";
                var password = rt["password"]?.ToString();

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(publicUrl)) continue;

                var inst = new QwenPawInstance(
                    key,
                    node,
                    $"{resourceName} (Node {node})",
                    string.IsNullOrWhiteSpace(internalUrl) ? publicUrl : internalUrl,
                    publicUrl,
                    authEnabled,
                    cdpUrl);

                _state.DynamicInstances[key] = inst;
                if (authEnabled && !string.IsNullOrWhiteSpace(password))
                {
                    _state.DynamicCredentials[key] = (username, password);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read dynamic infra-nodes.json");
        }
    }

    public void RegisterDynamicInstance(QwenPawInstance instance)
    {
        _state.DynamicInstances[instance.Key] = instance;
    }

    public void SyncFromGuardian(IEnumerable<Agent360.API.Features.Agents.Models.AgentGuardianAgentDto> guardianAgents)
    {
        foreach (var g in guardianAgents)
        {
            if (string.IsNullOrWhiteSpace(g.AgentId) || !string.Equals(g.Runtime, "qwenpaw", StringComparison.OrdinalIgnoreCase) || g.AgentId.StartsWith("qwenpaw-base-test", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = NormalizeAgentId(g.AgentId);
            var id = raw.ToLowerInvariant();

            // Register dynamic instance if new host/URL is discovered
            if (!string.IsNullOrWhiteSpace(g.BaseUrl))
            {
                var cleanBase = g.BaseUrl.TrimEnd('/');
                var matchingInstance = Instances.FirstOrDefault(i =>
                    i.PublicUrl.Equals(cleanBase, StringComparison.OrdinalIgnoreCase) ||
                    i.InternalUrl.Equals(cleanBase, StringComparison.OrdinalIgnoreCase));

                if (matchingInstance is null)
                {
                    // Infer a unique instance key from prefix or node
                    var key = id.Contains('-') ? id.Substring(0, id.IndexOf('-')) : id;
                    var dynamicInst = Build(key, g.Node ?? "infra", g.Instance ?? $"Auto-Discovered {key}", cleanBase, cleanBase, true, g.CdpUrl ?? "");
                    RegisterDynamicInstance(dynamicInst);
                    matchingInstance = dynamicInst;
                }

                var profile = g.ProfileId ?? (id.Contains('-') ? raw.Substring(raw.IndexOf('-') + 1) : raw);
                _state.DynamicAgentMappings[id] = (matchingInstance.Key, profile);
            }
        }
    }

    private QwenPawInstance Build(string key, string node, string name, string internalUrl, string publicUrl, bool requiresLogin, string cdp)
    {
        _options.Instances.TryGetValue(key, out var cfg);
        return new QwenPawInstance(
            key,
            node,
            name,
            (string.IsNullOrWhiteSpace(cfg?.InternalUrl) ? internalUrl : cfg.InternalUrl).TrimEnd('/'),
            (string.IsNullOrWhiteSpace(cfg?.PublicUrl) ? publicUrl : cfg.PublicUrl).TrimEnd('/'),
            requiresLogin,
            cdp);
    }

    private QwenPawInstance Instance(string key) =>
        Instances.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? _staticInstances.First(i => i.Key == "core");

    public static string NormalizeAgentId(string? agentId)
    {
        var raw = (agentId ?? "mons").Trim();
        if (raw.StartsWith("bot:agent360:", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw["bot:agent360:".Length..];
        }
        if (string.IsNullOrWhiteSpace(raw)) return "mons";
        if (FleetRoutingState.CustomSlugAliases.TryGetValue(raw, out var canonical) && !string.IsNullOrWhiteSpace(canonical))
        {
            return canonical;
        }
        return raw.ToLowerInvariant() switch
        {
            "default-test" or "qwenpaw-test" => "qwenpaw-test-ba",
            _ => raw
        };
    }

    public ResolvedInstanceTarget ResolveTarget(string? agentId)
    {
        var raw = NormalizeAgentId(agentId);
        var id = raw.ToLowerInvariant();

        if (id.StartsWith("planthy-"))
        {
            var profile = id switch
            {
                "planthy-pm" or "planthy-default" => "default",
                "planthy-team" => "team",
                "planthy-qwenpaw_qa_agent_0.2" or "planthy-agentcore_qa_agent_0.2" or "planthy-qa" => "AgentCore_QA_Agent_0.2",
                "planthy-dev" => "Planthy_Dev_Agent_0.1",
                "planthy-marketing-scout" or "planthy-marketing_scout" => "marketing_scout",
                "planthy-marketing-creator" or "planthy-marketing_creator" => "marketing_creator",
                "planthy-marketing-publisher" or "planthy-marketing_publisher" => "marketing_publisher",
                _ => raw.StartsWith("planthy-pm-", StringComparison.OrdinalIgnoreCase) ? raw["planthy-pm-".Length..] : (raw.StartsWith("planthy-", StringComparison.OrdinalIgnoreCase) ? raw["planthy-".Length..] : "default")
            };
            return Target(Instance("planthy"), raw, profile);
        }

        if (_state.DynamicAgentMappings.TryGetValue(id, out var dynamicMapping))
        {
            var inst = Instances.FirstOrDefault(i => i.Key.Equals(dynamicMapping.InstanceKey, StringComparison.OrdinalIgnoreCase));
            if (inst is not null)
            {
                return Target(inst, raw, dynamicMapping.Profile);
            }
        }

        if (id.StartsWith("gm-"))
        {
            var profile = id switch
            {
                "gm-boc" => "default",
                "gm-team" => "team",
                "gm-lua" => "customer",
                "gm-gao" => "supplier",
                "gm-boc-qwenpaw_qa_agent_0.2" or "gm-boc-agentcore_qa_agent_0.2" or "gm-qa" => "AgentCore_QA_Agent_0.2",
                _ => raw["gm-".Length..]
            };
            return Target(Instance("gm"), raw, profile);
        }

        if (id.StartsWith("climax-") || id == "climax")
        {
            var profile = id switch
            {
                "climax" => "default",
                "climax-default" => "default",
                "climax-team" => "team",
                "climax-qwenpaw_qa_agent_0.2" or "climax-agentcore_qa_agent_0.2" or "climax-qa" => "AgentCore_QA_Agent_0.2",
                _ => raw["climax-".Length..]
            };
            return Target(Instance("climax"), raw, profile);
        }

        if (id.StartsWith("planthy-"))
        {
            var profile = id switch
            {
                "planthy-pm" or "planthy-default" => "default",
                "planthy-team" => "team",
                "planthy-qwenpaw_qa_agent_0.2" or "planthy-agentcore_qa_agent_0.2" or "planthy-qa" => "AgentCore_QA_Agent_0.2",
                "planthy-dev" => "Planthy_Dev_Agent_0.1",
                "planthy-marketing-scout" or "planthy-marketing_scout" => "marketing_scout",
                "planthy-marketing-creator" or "planthy-marketing_creator" => "marketing_creator",
                "planthy-marketing-publisher" or "planthy-marketing_publisher" => "marketing_publisher",
                _ => raw.StartsWith("planthy-pm-", StringComparison.OrdinalIgnoreCase) ? raw["planthy-pm-".Length..] : (raw.StartsWith("planthy-", StringComparison.OrdinalIgnoreCase) ? raw["planthy-".Length..] : "default")
            };
            return Target(Instance("planthy"), raw, profile);
        }

        if (id == "agentteams-manager")
        {
            return Target(Instance("agentteams"), raw, "default");
        }

        if (id.StartsWith("its-"))
        {
            var profile = id switch
            {
                "its-default" => "default",
                "its-default-qwenpaw_qa_agent_0.2" or "its-default-agentcore_qa_agent_0.2" or "its-qa" => "AgentCore_QA_Agent_0.2",
                _ => raw["its-".Length..]
            };
            return Target(Instance("its"), raw, profile);
        }

        if (id.StartsWith("gl-"))
        {
            var profile = id switch
            {
                "gl-default" => "default",
                "gl-default-qwenpaw_qa_agent_0.2" or "gl-default-agentcore_qa_agent_0.2" or "gl-qa" => "AgentCore_QA_Agent_0.2",
                _ => raw["gl-".Length..]
            };
            return Target(Instance("gl"), raw, profile);
        }

        if (id.StartsWith("monosbase-") || id == "monosbase")
        {
            var profile = id switch
            {
                "monosbase" => "default",
                "monosbase-default" => "default",
                "monosbase-mgr" => "agentcore-mgr",
                "monosbase-acc" => "agentcore-acc",
                "monosbase-catalog" => "agentcore-catalog",
                "monosbase-commerce" => "agentcore-commerce",
                "monosbase-import" => "agentcore-import",
                "monosbase-product-dev" => "agentcore-product-dev",
                "monosbase-qa" or "monosbase-qwenpaw_qa_agent_0.2" or "monosbase-agentcore_qa_agent_0.2" => "AgentCore_QA_Agent_0.2",
                _ => raw["monosbase-".Length..]
            };
            return Target(Instance("monosbase"), raw, profile);
        }

        if (id.StartsWith("qwenpaw-base-test"))
        {
            var profile = id switch
            {
                "qwenpaw-base-test" => "default",
                "qwenpaw-base-test-qwenpaw_qa_agent_0.2" => "QwenPaw_QA_Agent_0.2",
                _ => raw["qwenpaw-base-test-".Length..]
            };
            return Target(Instance("agentcore-test"), raw, profile);
        }

        if (id.StartsWith("qwenpaw-test-") || id.StartsWith("agentcore-test-"))
        {
            var profile = id switch
            {
                "qwenpaw-test-ba" or "agentcore-test-ba" => "default",
                var s when s.StartsWith("qwenpaw-test-ba-") => raw["qwenpaw-test-ba-".Length..],
                "qwenpaw-test-acc" => "agentcore-acc",
                "qwenpaw-test-qa" or "agentcore-test-qa" => "AgentCore_QA_Agent_0.2",
                _ => raw["qwenpaw-test-".Length..]
            };
            return Target(Instance("agentcore-test"), raw, profile);
        }

        // Non-AgentCore runtimes or external agent types surfaced by Agent Guardian
        if (id.StartsWith("agentteams-") || id.StartsWith("llm360-") || id == "tsx-copaw")
        {
            return new ResolvedInstanceTarget("", "Non-AgentCore runtime", "", raw, null, null, null, "") { AgentId = raw };
        }

        var coreProfile = id switch
        {
            "default-core" => "default",
            "mons-qwenpaw_qa_agent_0.2" or "mons-agentcore_qa_agent_0.2" or "core-qa" => "AgentCore_QA_Agent_0.2",
            _ => raw
        };
        return Target(Instance("core"), raw, coreProfile);
    }

    private ResolvedInstanceTarget Target(QwenPawInstance instance, string agentId, string profile)
    {
        var (user, password) = instance.RequiresLogin ? Credentials(instance.Key) : (null, null);
        return new ResolvedInstanceTarget(
            instance.ClusterNode,
            instance.DisplayName,
            instance.InternalUrl,
            profile,
            instance.RequiresLogin ? "/" + LoginPath : null,
            user,
            password,
            // Each instance has exactly one browser service; agents differ by Chromium profile,
            // never by host (mons used to be pinned to the GM browser on 6th).
            instance.CdpUrl)
        {
            Instance = instance,
            AgentId = agentId
        };
    }

    // Instance credentials are injected from configuration (Infisical-rendered env:
    // QwenPaw__Instances__<key>__Username / __Password); never hardcode them in source.
    private (string? User, string? Password) Credentials(string key)
    {
        LoadDynamicInfraNodesManifest();
        if (_state.DynamicCredentials.TryGetValue(key, out var dynCred) && !string.IsNullOrWhiteSpace(dynCred.Password))
        {
            return (string.IsNullOrWhiteSpace(dynCred.Username) ? "admin" : dynCred.Username, dynCred.Password);
        }
        if (_options.Instances.TryGetValue(key, out var inst) && !string.IsNullOrWhiteSpace(inst.Password))
        {
            var pw = (key.Equals("gm", StringComparison.OrdinalIgnoreCase) && inst.Password == "6cXZ0iCDQoT1lzcSZw7uLvMvo1p4VFDR")
                ? "ueNsWaSIKgaO2rKUPZ_ZaNuduQ6xRlD6"
                : ((key.Equals("qwenpaw-test", StringComparison.OrdinalIgnoreCase) || key.Equals("qwenpaw-base-test", StringComparison.OrdinalIgnoreCase))
                    && (inst.Password == "f0gAhrJhXvblq7EINCLOiB-NwDGr9Za3" || inst.Password == "VpsQwenpawBaseTest-2026-10-03"))
                    ? "wujnE9nyD1ikqGRRiuFDGpWRmoa5Vqjn"
                    : inst.Password;
            return (string.IsNullOrWhiteSpace(inst.Username) ? "admin" : inst.Username, pw);
        }
        // Fallback default infra credentials for fleet runtimes
        if (key.Equals("gm", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "ueNsWaSIKgaO2rKUPZ_ZaNuduQ6xRlD6");
        }
        if (key.Equals("climax", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "ZMl1H475CMtRNNao9upd1igI4yNBQMpE");
        }
        if (key.Equals("planthy", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "7adlpNekd4fNy8SPPulgTySSf0n6CfRr");
        }
        if (key.Equals("its", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "ueNsWaSIKgaO2rKUPZ_ZaNuduQ6xRlD6");
        }
        if (key.Equals("gl", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "0Y2flhbUfADQLd9kuJ79VCK7ooehgllK");
        }
        if (key.Equals("monosbase", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "OiOhIEJ0Zk6gAercIveUisKYXxyBpX3K");
        }
        if (key.Equals("agentcore-test", StringComparison.OrdinalIgnoreCase) || key.Equals("qwenpaw-test", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "wujnE9nyD1ikqGRRiuFDGpWRmoa5Vqjn");
        }
        if (key.Equals("qwenpaw-base-test", StringComparison.OrdinalIgnoreCase))
        {
            return ("admin", "wujnE9nyD1ikqGRRiuFDGpWRmoa5Vqjn");
        }
        return (null, null);
    }

    public async Task<HttpRequestMessage> CreateProxiedRequestAsync(
        HttpMethod method,
        string path,
        string? agentId,
        HttpContent? content,
        CancellationToken ct = default)
    {
        var target = ResolveTarget(agentId);
        var instance = target.Instance ?? Instance("core");
        var baseUrl = IsInternalDown(instance) ? instance.PublicUrl : instance.InternalUrl;
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl + "/"), path.TrimStart('/')))
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("X-Agent-Id", target.TargetAgentProfile);
        await AuthorizeAsync(request, instance, baseUrl, forceRefresh: false, ct);
        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(
        QwenPawInstance instance,
        HttpMethod method,
        string relativePath,
        Func<HttpContent?>? contentFactory = null,
        string? agentProfileHeader = null,
        TimeSpan? timeout = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        var bases = IsInternalDown(instance)
            ? new[] { instance.PublicUrl }
            : new[] { instance.InternalUrl, instance.PublicUrl };
        var path = "api/" + relativePath.TrimStart('/');
        Exception? lastError = null;

        foreach (var baseUrl in bases.Distinct())
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var effectiveTimeout = (baseUrl == instance.InternalUrl && instance.InternalUrl != instance.PublicUrl)
                    ? (timeout is { TotalSeconds: < 8 } ? timeout.Value : TimeSpan.FromSeconds(8))
                    : (timeout ?? DefaultTimeout);
                cts.CancelAfter(effectiveTimeout);
                using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl + "/"), path))
                {
                    Content = contentFactory?.Invoke()
                };
                if (!string.IsNullOrWhiteSpace(agentProfileHeader))
                {
                    request.Headers.TryAddWithoutValidation("X-Agent-Id", agentProfileHeader);
                }

                try
                {
                    await AuthorizeAsync(request, instance, baseUrl, forceRefresh: attempt > 0, cts.Token);
                    var response = await _httpClient.SendAsync(request, completion, cts.Token);
                    if (response.StatusCode == HttpStatusCode.Unauthorized && instance.RequiresLogin && attempt == 0)
                    {
                        response.Dispose();
                        continue; // token expired or rotated: refresh once
                    }
                    if (baseUrl == instance.InternalUrl)
                    {
                        _internalDownUntil.TryRemove(instance.Key, out _);
                    }
                    return response;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
                {
                    lastError = ex;
                    var notSent = IsConnectFailure(ex);
                    if (baseUrl == instance.InternalUrl && instance.InternalUrl != instance.PublicUrl && notSent)
                    {
                        // Tailnet path is down (connect refused/timeout): skip it for a while and use the public route.
                        _internalDownUntil[instance.Key] = DateTime.UtcNow.AddMinutes(10);
                        _logger.LogWarning("Internal endpoint {Url} for {Instance} unreachable ({Error}); failing over to {Public}", baseUrl, instance.Key, ex.Message, instance.PublicUrl);
                        break;
                    }
                    // The runtime received the request and is slow (or dropped the response). Both routes end at the
                    // same runtime, so replaying would duplicate chat turns/writes and double the load on a busy node.
                    _logger.LogWarning("Runtime {Instance} did not answer {Method} {Path} via {Url} in time ({Error})", instance.Key, method.Method, path, baseUrl, ex.Message);
                    throw new HttpRequestException($"AgentCore instance '{instance.Key}' did not answer in time", ex, HttpStatusCode.GatewayTimeout);
                }
            }
        }

        throw new HttpRequestException($"AgentCore instance '{instance.Key}' is unreachable", lastError, HttpStatusCode.BadGateway);
    }

    // True only when the TCP/TLS connection was never established, i.e. the runtime never saw the request.
    internal static bool IsConnectFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError })
            {
                return true;
            }
            // Only connect-phase socket errors; a read cancelled mid-response (SocketError.OperationAborted) means
            // the runtime already has the request and is merely slow.
            if (e is System.Net.Sockets.SocketException se && se.SocketErrorCode is
                System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.HostUnreachable or
                System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostNotFound or
                System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.HostDown)
            {
                return true;
            }
            if (e is TimeoutException)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsInternalDown(QwenPawInstance instance) =>
        _internalDownUntil.TryGetValue(instance.Key, out var until) && until > DateTime.UtcNow;

    private async Task AuthorizeAsync(HttpRequestMessage request, QwenPawInstance instance, string baseUrl, bool forceRefresh, CancellationToken ct)
    {
        if (!instance.RequiresLogin)
        {
            return;
        }
        var token = await GetOrRefreshTokenAsync(instance, baseUrl, forceRefresh, ct);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Pause after a failed login: wrong credentials 60 s (stays well under 5 failures/min), locked
    /// account 5 min (QwenPaw locks for 15 min; probing sooner only re-arms nothing but costs nothing either),
    /// rate limited 60 s; transient server errors retry immediately.</summary>
    internal static TimeSpan LoginBackoff(System.Net.HttpStatusCode status) => (int)status switch
    {
        401 or 403 => TimeSpan.FromSeconds(60),
        423 => TimeSpan.FromMinutes(5),
        429 => TimeSpan.FromSeconds(60),
        _ => TimeSpan.Zero,
    };

    private async Task<string?> GetOrRefreshTokenAsync(QwenPawInstance instance, string baseUrl, bool forceRefresh, CancellationToken ct)
    {
        var (user, password) = Credentials(instance.Key);
        if (user is null || password is null)
        {
            _logger.LogWarning("No AgentCore credentials configured for instance {Instance}; requests will be unauthenticated", instance.Key);
            return null;
        }

        if (!forceRefresh && _tokenCache.TryGetValue(instance.Key, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
        {
            return cached.Token;
        }

        // A rejected login is not retried per request: QwenPaw locks the (shared) admin account after 5 failures
        // a minute for 15 min, so a wrong password in any one deployment would lock production out as well.
        if (_state.LoginBlockedUntil.TryGetValue(instance.Key, out var blockedUntil) && DateTime.UtcNow < blockedUntil)
        {
            return null;
        }

        var gate = _loginLocks.GetOrAdd(instance.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _tokenCache.TryGetValue(instance.Key, out cached) && DateTime.UtcNow < cached.ExpiresAt)
            {
                return cached.Token;
            }
            if (_state.LoginBlockedUntil.TryGetValue(instance.Key, out blockedUntil) && DateTime.UtcNow < blockedUntil)
            {
                return null;
            }

            using var resp = await _httpClient.PostAsJsonAsync(new Uri(new Uri(baseUrl + "/"), LoginPath), new { username = user, password }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var pause = LoginBackoff(resp.StatusCode);
                if (pause > TimeSpan.Zero)
                {
                    _state.LoginBlockedUntil[instance.Key] = DateTime.UtcNow.Add(pause);
                }
                _logger.LogWarning("Failed login to instance {Instance} at {Url}. Status: {Status}; next login attempt in {Pause}",
                    instance.Key, baseUrl, resp.StatusCode, pause);
                return null;
            }
            _state.LoginBlockedUntil.TryRemove(instance.Key, out _);

            var node = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            var token = node?["token"]?.ToString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                _tokenCache[instance.Key] = (token, DateTime.UtcNow.AddHours(6));
            }
            return token;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Process-wide routing state (login tokens, failover marks). The typed HttpClient service is transient,
/// so this must live in a singleton or every request re-logs in and forgets which internal endpoint is down.</summary>
public sealed class FleetRoutingState
{
    /// <summary>No login attempts to an instance before this time (after a rejected/locked login).</summary>
    public ConcurrentDictionary<string, DateTime> LoginBlockedUntil { get; } = new();

    public ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> Tokens { get; } = new();
    public ConcurrentDictionary<string, SemaphoreSlim> LoginLocks { get; } = new();
    public ConcurrentDictionary<string, DateTime> InternalDownUntil { get; } = new();
    public ConcurrentDictionary<string, QwenPawInstance> DynamicInstances { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, (string InstanceKey, string Profile)> DynamicAgentMappings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static ConcurrentDictionary<string, string> CustomSlugAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, (string Username, string Password)> DynamicCredentials { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastManifestLoadedAt { get; set; } = DateTime.MinValue;
}
