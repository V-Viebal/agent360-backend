using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Services;

/// <summary>
/// Single gateway from Agent360 to every QwenPaw runtime in the fleet. Every agent-scoped call is
/// resolved through <see cref="IFleetInstanceRoutingService"/> (internal Tailscale endpoint first,
/// public fallback, per-instance auth) and addressed as <c>/api/agents/{profile}/...</c>.
/// </summary>
public sealed class QwenPawClient : IQwenPawClient
{
    private const string GuardianApiUrl = "https://agent-guardian.hmz.one/api/agents";
    private const string AgentsCacheKey = "agent360:agents:v2";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SlowReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim AgentsRefreshGate = new(1, 1);

    private readonly HttpClient _httpClient;
    private readonly IFleetInstanceRoutingService _routingService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<QwenPawClient> _logger;

    public QwenPawClient(
        HttpClient httpClient,
        IFleetInstanceRoutingService routingService,
        IOptions<QwenPawOptions> options,
        ILogger<QwenPawClient> logger,
        IMemoryCache? cache = null)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _routingService = routingService;
        _logger = logger;
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions());
    }

    // ------------------------------------------------------------------
    // Transport helpers
    // ------------------------------------------------------------------

    private ResolvedInstanceTarget Resolve(string? agentId) => _routingService.ResolveTarget(agentId);

    private static string AgentPath(ResolvedInstanceTarget target, string relative) =>
        $"agents/{Uri.EscapeDataString(target.TargetAgentProfile)}" + (string.IsNullOrEmpty(relative) ? "" : "/" + relative.TrimStart('/'));

    private Task<HttpResponseMessage> SendAsync(
        ResolvedInstanceTarget target,
        HttpMethod method,
        string relative,
        Func<HttpContent?>? content = null,
        bool global = false,
        TimeSpan? timeout = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        if (target.Instance is null)
        {
            throw new HttpRequestException($"Agent '{target.AgentId}' is not hosted on an AgentCore runtime", null, HttpStatusCode.NotFound);
        }

        return _routingService.SendAsync(
            target.Instance,
            method,
            global ? relative : AgentPath(target, relative),
            content,
            target.TargetAgentProfile,
            timeout ?? ReadTimeout,
            completion,
            ct);
    }

    private async Task<string> GetStringAsync(string? agentId, string relative, TimeSpan? timeout = null, bool global = false, CancellationToken ct = default)
    {
        using var response = await SendAsync(Resolve(agentId), HttpMethod.Get, relative, global: global, timeout: timeout, ct: ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<T?> GetJsonAsync<T>(string? agentId, string relative, bool global = false, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(Resolve(agentId), HttpMethod.Get, relative, global: global, timeout: timeout, ct: ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private Task<T> CachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory) =>
        _cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            return factory();
        })!;

    // ------------------------------------------------------------------
    // Agents
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<AgentSummaryResponse>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(AgentsCacheKey, out IReadOnlyList<AgentSummaryResponse>? fresh) && fresh is { Count: > 0 })
        {
            return fresh;
        }

        await AgentsRefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(AgentsCacheKey, out fresh) && fresh is { Count: > 0 })
            {
                return fresh;
            }

            var agents = await LoadFleetAgentsAsync(cancellationToken);
            if (agents.Count > 0)
            {
                _cache.Set(AgentsCacheKey, agents, TimeSpan.FromSeconds(20));
                _cache.Set(AgentsCacheKey + ":last-good", agents, TimeSpan.FromHours(6));
                return agents;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Fleet agent discovery failed");
        }
        finally
        {
            AgentsRefreshGate.Release();
        }

        // Serve the last good snapshot rather than an empty fleet when discovery is temporarily down.
        if (_cache.TryGetValue(AgentsCacheKey + ":last-good", out IReadOnlyList<AgentSummaryResponse>? stale) && stale is not null)
        {
            return stale;
        }
        return Array.Empty<AgentSummaryResponse>();
    }

    /// <summary>Reloads the fleet snapshot unconditionally; used by the background warmer so user calls stay on cache.</summary>
    public async Task<IReadOnlyList<AgentSummaryResponse>> RefreshAgentsAsync(CancellationToken cancellationToken = default)
    {
        await AgentsRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var agents = await LoadFleetAgentsAsync(cancellationToken);
            if (agents.Count > 0)
            {
                _cache.Set(AgentsCacheKey, agents, TimeSpan.FromSeconds(60));
                _cache.Set(AgentsCacheKey + ":last-good", agents, TimeSpan.FromHours(6));
            }
            return agents;
        }
        finally
        {
            AgentsRefreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<AgentSummaryResponse>> LoadFleetAgentsAsync(CancellationToken ct)
    {
        var guardianTask = LoadAgentsFromGuardianAsync(ct);
        var runtimeTasks = _routingService.Instances.ToDictionary(i => i.Key, i => LoadRuntimeAgentsAsync(i, ct));
        await Task.WhenAll(runtimeTasks.Values.Cast<Task>().Append(guardianTask));

        var runtime = new Dictionary<string, QwenPawAgentDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, task) in runtimeTasks)
        {
            foreach (var dto in task.Result)
            {
                runtime[Agent360Slug(key, dto.Id)] = dto;
            }
        }

        var guardian = guardianTask.Result;
        var results = new List<AgentSummaryResponse>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in guardian)
        {
            seen.Add(g.Slug);
            results.Add(runtime.TryGetValue(g.Slug, out var live) ? MergeRuntime(g, live) : g);
        }

        // Runtime agents that Agent Guardian does not track yet are still loaded.
        foreach (var (slug, dto) in runtime)
        {
            if (seen.Contains(slug))
            {
                continue;
            }
            results.Add(FromRuntime(slug, dto));
        }

        return results.OrderByDescending(a => a.Pinned).ThenBy(a => a.Slug, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Agent360Slug(string instanceKey, string profileId) => instanceKey switch
    {
        "its" => profileId == "default" ? "its-default" : $"its-default-{profileId}",
        "gl" => profileId == "default" ? "gl-default" : $"gl-default-{profileId}",
        "gm" => profileId switch
        {
            "default" => "gm-boc",
            "customer" => "gm-lua",
            "supplier" => "gm-gao",
            _ => $"gm-{profileId}"
        },
        "climax" => $"climax-{profileId}",
        "planthy" => profileId == "default" ? "planthy-pm" : $"planthy-{profileId}",
        "agentteams" => profileId == "default" ? "agentteams-manager" : $"agentteams-manager-{profileId}",
        "qwenpaw-test" => profileId switch
        {
            "default" => "qwenpaw-test-ba",
            "agentcore-acc" => "qwenpaw-test-acc",
            "QwenPaw_QA_Agent_0.2" => "qwenpaw-test-qa",
            _ => $"qwenpaw-test-ba-{profileId}"
        },
        "qwenpaw-base-test" => profileId == "default" ? "qwenpaw-base-test" : $"qwenpaw-base-test-{profileId}",
        _ => profileId switch
        {
            "default" => "default-core",
            "QwenPaw_QA_Agent_0.2" => "mons-QwenPaw_QA_Agent_0.2",
            _ => profileId
        }
    };

    // A runtime that blips must not drop its agents (or their enabled=false flag) from the fleet snapshot.
    private async Task<IReadOnlyList<QwenPawAgentDto>> LoadRuntimeAgentsAsync(QwenPawInstance instance, CancellationToken ct)
    {
        var key = $"agent360:runtime-agents:{instance.Key}:last-good";
        var agents = await FetchRuntimeAgentsAsync(instance, ct);
        if (agents is not null)
        {
            _cache.Set(key, agents, TimeSpan.FromHours(6));
            return agents;
        }
        return _cache.TryGetValue(key, out IReadOnlyList<QwenPawAgentDto>? lastGood) && lastGood is not null
            ? lastGood
            : Array.Empty<QwenPawAgentDto>();
    }

    private async Task<IReadOnlyList<QwenPawAgentDto>?> FetchRuntimeAgentsAsync(QwenPawInstance instance, CancellationToken ct)
    {
        try
        {
            using var response = await _routingService.SendAsync(instance, HttpMethod.Get, "agents", timeout: TimeSpan.FromSeconds(6), ct: ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Runtime {Instance} returned {Status} listing agents", instance.Key, response.StatusCode);
                return null;
            }
            var list = await response.Content.ReadFromJsonAsync<QwenPawAgentListResponse>(ct);
            return list?.Agents;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Runtime {Instance} unavailable while listing agents", instance.Key);
            return null;
        }
    }

    private static string RuntimeModel(QwenPawAgentDto dto, string? fallback)
    {
        var model = dto.ActiveModel?.Model ?? dto.BackendModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return fallback ?? "unknown";
        }
        return string.IsNullOrWhiteSpace(dto.ActiveModel?.ProviderId) ? model : $"{dto.ActiveModel.ProviderId}/{model}";
    }

    private static AgentSummaryResponse MergeRuntime(AgentSummaryResponse guardian, QwenPawAgentDto live) => guardian with
    {
        Model = RuntimeModel(live, guardian.Model),
        Enabled = live.Enabled,
        Status = !live.Enabled ? "disabled" : guardian.Status,
        Description = string.IsNullOrWhiteSpace(guardian.Description) ? live.Description : guardian.Description,
        AvailableInChat = live.Enabled && live.AvailableInChat
    };

    private static bool IsRootDefaultAgent(string slug)
    {
        var s = slug.ToLowerInvariant().Trim();
        return s is "default-core" or "gm-boc" or "climax-default" or "its-default" or "gl-default" or "planthy-pm"
            || s.EndsWith("-default") || s == "default";
    }

    private AgentSummaryResponse FromRuntime(string slug, QwenPawAgentDto a)
    {
        var target = Resolve(slug);
        var (friendlyName, roleTag) = MapAgentIdentity(slug, a.Description, "skyer");
        return new AgentSummaryResponse(
            Id: $"bot:agent360:{slug}",
            Name: friendlyName == slug && !string.IsNullOrWhiteSpace(a.Name) ? a.Name : friendlyName,
            Slug: slug,
            RoleTag: roleTag,
            Node: FormatNodeDisplay(target.ClusterNode, target.InstanceName),
            Status: !a.Enabled ? "disabled" : a.StartupStatus switch { "pending" => "evaluating", "disabled" => "disabled", _ => "active" },
            Model: RuntimeModel(a, null),
            RuntimeVersion: "Skyer v2.2.1",
            Uptime: "Online (Runtime)",
            Description: a.Description,
            Enabled: a.Enabled,
            Pinned: a.Pinned,
            AvailableInChat: a.AvailableInChat,
            Tools: DefaultTools,
            CanToggle: !IsRootDefaultAgent(slug),
            IsRootAgent: IsRootDefaultAgent(slug));
    }

    private const string GuardianLastGoodKey = "agent360:guardian:last-good";

    // Guardian is the only source for non-QwenPaw agents; reuse its last good list when it blips so
    // the fleet snapshot never shrinks to runtime-only agents.
    private async Task<IReadOnlyList<AgentSummaryResponse>> LoadAgentsFromGuardianAsync(CancellationToken cancellationToken)
    {
        var agents = await FetchGuardianAgentsAsync(cancellationToken);
        if (agents.Count > 0)
        {
            _cache.Set(GuardianLastGoodKey, agents, TimeSpan.FromHours(6));
            return agents;
        }
        return _cache.TryGetValue(GuardianLastGoodKey, out IReadOnlyList<AgentSummaryResponse>? lastGood) && lastGood is not null
            ? lastGood
            : agents;
    }

    private async Task<IReadOnlyList<AgentSummaryResponse>> FetchGuardianAgentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GuardianApiUrl);
            request.Headers.Add("User-Agent", "Agent360/1.0");
            request.Headers.Add("Accept", "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<AgentSummaryResponse>();
            }

            var guardianData = await response.Content.ReadFromJsonAsync<AgentGuardianListResponse>(cancellationToken: cts.Token);
            if (guardianData?.Agents is null or { Count: 0 })
            {
                return Array.Empty<AgentSummaryResponse>();
            }

            // Dynamically register discovered instances and mappings into FleetInstanceRoutingService
            _routingService.SyncFromGuardian(guardianData.Agents);

            var results = new List<AgentSummaryResponse>(guardianData.Agents.Count);
            foreach (var g in guardianData.Agents)
            {
                if (g.AgentId.StartsWith("qwenpaw-base-test", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Deduplicate Node 7th alias (ai-qwenpaw-base-test.antares.vn -> ai-test.antares.vn)
                }
                var isConfigured = !string.Equals(g.State, "NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase);
                var status = g.State switch
                {
                    "AVAILABLE" => "active",
                    "DEGRADED" => "evaluating",
                    "NOT_CONFIGURED" => "idle",
                    _ => isConfigured ? "active" : "idle"
                };

                var (friendlyName, roleTag) = MapAgentIdentity(g.AgentId, g.Description, g.Runtime);
                results.Add(new AgentSummaryResponse(
                    Id: $"bot:agent360:{g.AgentId}",
                    Name: friendlyName,
                    Slug: g.AgentId,
                    RoleTag: roleTag,
                    Node: FormatNodeDisplay(g.Node, g.Instance),
                    Status: status,
                    Model: ExtractModel(g),
                    RuntimeVersion: "Skyer v2.2.1",
                    Uptime: "Online (Fleet)",
                    Description: g.Description,
                    Enabled: isConfigured,
                    Pinned: g.AgentId is "mons" or "ken" or "zen" or "gm-boc" or "default-core",
                    AvailableInChat: true,
                    Tools: ExtractTools(g),
                    CanToggle: !IsRootDefaultAgent(g.AgentId),
                    IsRootAgent: IsRootDefaultAgent(g.AgentId)));
            }

            return results;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Agent Guardian discovery unavailable; using runtime discovery only");
            return Array.Empty<AgentSummaryResponse>();
        }
    }

    public async Task<string> GetAgentProfileRawAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var slug = FleetInstanceRoutingService.NormalizeAgentId(agentId);
        if (!Resolve(slug).IsQwenPaw)
        {
            // Non-AgentCore runtimes (AgentTeams gateway, LLM360) expose their profile through fleet discovery.
            var agents = await GetAgentsAsync(cancellationToken);
            var match = agents.FirstOrDefault(a => a.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase))
                ?? throw new HttpRequestException($"Agent '{slug}' not found in fleet", null, HttpStatusCode.NotFound);
            return JsonSerializer.Serialize(new
            {
                id = match.Slug,
                name = match.Name,
                description = match.Description,
                runtime = match.RuntimeVersion,
                node = match.Node,
                status = match.Status,
                enabled = match.Enabled,
                model = match.Model,
                runtime_api = false,
                tools = match.Tools
            });
        }

        return await CachedAsync($"agent360:profile:{slug}", TimeSpan.FromSeconds(10),
            () => GetStringAsync(slug, "", ct: cancellationToken));
    }

    private static string ResolveInstanceDefaultProvider(string? instanceKey, string? requestedProvider)
    {
        var key = (instanceKey ?? "core").ToLowerInvariant();
        var defaultProvider = key switch
        {
            "gm" => "llm360-gm",
            "climax" => "llm360",
            "planthy" => "llm360",
            "its" => "llm360-its",
            "gl" => "llm360-gl",
            _ => "llm360coding"
        };

        if (string.IsNullOrWhiteSpace(requestedProvider))
        {
            return defaultProvider;
        }

        var req = requestedProvider.Trim();
        if (req.StartsWith("llm360", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(req, "opencode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(req, "kilo", StringComparison.OrdinalIgnoreCase))
        {
            return defaultProvider;
        }

        return req;
    }

    public async Task<SetActiveModelResponseDto?> GetAgentActiveModelAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var target = Resolve(agentId);
        if (target.IsQwenPaw)
        {
            try
            {
                using var response = await SendAsync(target, HttpMethod.Get, "models/active", global: true, timeout: ReadTimeout, ct: cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var dto = await response.Content.ReadFromJsonAsync<SetActiveModelResponseDto>(cancellationToken: cancellationToken);
                    if (dto is not null)
                    {
                        return dto;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Direct models/active lookup failed for {AgentId}; falling back to fleet snapshot", agentId);
            }
        }

        var agents = await GetAgentsAsync(cancellationToken);
        var cleanSlug = FleetInstanceRoutingService.NormalizeAgentId(agentId);
        var match = agents.FirstOrDefault(a => a.Slug.Equals(cleanSlug, StringComparison.OrdinalIgnoreCase) || a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var rawModel = match.Model ?? "";
        var prov = rawModel.Contains('/') ? rawModel.Split('/')[0] : "";
        var mod = rawModel.Contains('/') ? rawModel.Split('/')[1] : rawModel;
        return new SetActiveModelResponseDto(new QwenPawActiveModel(prov, mod), 500000);
    }

    public async Task<SetActiveModelResponseDto?> SetAgentActiveModelAsync(string agentId, string providerId, string model, CancellationToken cancellationToken = default)
    {
        var target = Resolve(agentId);
        var cleanModel = model.Contains('/') ? model.Split('/').Last() : model;
        var effectiveProvider = ResolveInstanceDefaultProvider(target.Instance?.Key, providerId);
        var payload = new SetActiveModelRequestDto("agent", target.TargetAgentProfile, effectiveProvider, cleanModel);
        using var response = await SendAsync(target, HttpMethod.Put, "models/active", () => JsonContent.Create(payload), global: true, timeout: SlowReadTimeout, ct: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to update active model for agent {AgentId}. StatusCode: {StatusCode}", agentId, response.StatusCode);
            return null;
        }

        InvalidateAgent(agentId);
        if (target.Instance is not null)
        {
            _cache.Remove($"agent360:runtime-agents:{target.Instance.Key}:last-good");
        }
        return await response.Content.ReadFromJsonAsync<SetActiveModelResponseDto>(cancellationToken: cancellationToken);
    }

    public async Task<ToggleAgentResponse?> ToggleAgentAsync(string agentId, bool enabled, CancellationToken cancellationToken = default)
    {
        var target = Resolve(agentId);
        var payload = new ToggleAgentRequest(enabled);
        using var response = await SendAsync(
            target,
            HttpMethod.Patch,
            "toggle",
            () => JsonContent.Create(payload),
            global: false,
            timeout: SlowReadTimeout,
            ct: cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to toggle agent {AgentId} to enabled={Enabled}. StatusCode: {StatusCode}", agentId, enabled, response.StatusCode);
            return null;
        }

        InvalidateAgent(agentId);
        if (target.Instance is not null)
        {
            _cache.Remove($"agent360:runtime-agents:{target.Instance.Key}:last-good");
        }

        return await response.Content.ReadFromJsonAsync<ToggleAgentResponse>(cancellationToken: cancellationToken);
    }

    private void InvalidateAgent(string agentId)
    {
        var normalized = FleetInstanceRoutingService.NormalizeAgentId(agentId);
        _cache.Remove($"agent360:profile:{normalized}");
        _cache.Remove(AgentsCacheKey);
        _cache.Remove(AgentsCacheKey + ":last-good");
    }

    public async Task<string> GetModelsAsync(CancellationToken cancellationToken = default) =>
        await CachedAsync("agent360:models", TimeSpan.FromMinutes(2), () => GetStringAsync("mons", "models", global: true, ct: cancellationToken));

    /// <summary>Provider catalog trimmed to id/name/model ids (no discovery metadata or key prefixes).</summary>
    public async Task<string> GetModelsCompactAsync(CancellationToken cancellationToken = default) =>
        await CachedAsync("agent360:models:compact", TimeSpan.FromMinutes(2), async () =>
        {
            var raw = await GetModelsAsync(cancellationToken);
            var providers = JsonNode.Parse(raw) as JsonArray ?? [];
            var compact = new JsonArray();
            foreach (var p in providers.OfType<JsonObject>())
            {
                var models = new JsonArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var list in new[] { "discovered_models", "extra_models", "models" })
                {
                    if (p[list] is not JsonArray arr)
                    {
                        continue;
                    }
                    foreach (var m in arr.OfType<JsonObject>())
                    {
                        var id = m["id"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                        {
                            continue;
                        }
                        models.Add(new JsonObject { ["id"] = id, ["name"] = m["name"]?.GetValue<string>() ?? id });
                    }
                }
                compact.Add(new JsonObject
                {
                    ["id"] = p["id"]?.GetValue<string>(),
                    ["name"] = p["name"]?.GetValue<string>(),
                    ["models"] = models
                });
            }
            return compact.ToJsonString();
        });

    // ------------------------------------------------------------------
    // Chats
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<AgentSessionResponse>> GetChatsAsync(CancellationToken cancellationToken = default)
    {
        var chats = await GetJsonAsync<IReadOnlyList<QwenPawChatDto>>("mons", "chats", ct: cancellationToken);
        return (chats ?? Array.Empty<QwenPawChatDto>()).Select(c => new AgentSessionResponse(
            Id: !string.IsNullOrWhiteSpace(c.Id) ? c.Id : (c.SessionId ?? ""),
            Title: string.IsNullOrWhiteSpace(c.Name) ? "Session" : c.Name,
            UpdatedAt: c.UpdatedAt ?? DateTimeOffset.UtcNow.ToString("O"),
            MessageCount: 1,
            LastMessageSnippet: c.Name,
            Unread: false)).ToList();
    }

    // Session lists are read on every workspace open; the core runtime can take seconds under load.
    // Serve a 10s fresh copy, coalesce concurrent loads, and fall back to the last good list on runtime errors.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ChatListGates = new();

    public async Task<string> GetAgentChatsRawAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var key = ChatListKey(agentId);
        if (_cache.TryGetValue(key, out string? fresh) && fresh is not null)
        {
            return fresh;
        }
        return await RefreshAgentChatsRawAsync(agentId, cancellationToken);
    }

    public async Task<string> RefreshAgentChatsRawAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var key = ChatListKey(agentId);
        var gate = ChatListGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var body = await GetStringAsync(agentId, "chats", timeout: ReadTimeout, ct: cancellationToken);
            _cache.Set(key, body, TimeSpan.FromSeconds(15));
            _cache.Set(key + ":last-good", body, TimeSpan.FromHours(6));
            return body;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && _cache.TryGetValue(key + ":last-good", out string? stale) && stale is not null)
        {
            _logger.LogWarning("Serving last good chat list for {Agent}: {Error}", agentId, ex.Message);
            return stale;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ChatListKey(string? agentId) =>
        $"agent360:chats:{FleetInstanceRoutingService.NormalizeAgentId(agentId).ToLowerInvariant()}";

    public void InvalidateChatList(string? agentId) => _cache.Remove(ChatListKey(agentId));

    private async Task<string?> ResolveChatUuidAsync(string identifier, string? agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (attempt == 1) InvalidateChatList(agentId);
                var chatsRaw = await GetAgentChatsRawAsync(agentId ?? "mons", ct);
                if (string.IsNullOrWhiteSpace(chatsRaw)) continue;
                using var doc = JsonDocument.Parse(chatsRaw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var sid = el.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
                    if (string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sid, identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        return !string.IsNullOrWhiteSpace(id) ? id : identifier;
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
        }
        return null;
    }

    public async Task<string> GetChatHistoryRawAsync(string chatId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var targetChatId = chatId;
        if (!Guid.TryParse(chatId, out _))
        {
            var resolved = await ResolveChatUuidAsync(chatId, agentId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                targetChatId = resolved;
            }
        }

        try
        {
            return await GetStringAsync(agentId, $"chats/{Uri.EscapeDataString(targetChatId)}", SlowReadTimeout, ct: cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && string.Equals(targetChatId, chatId, StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await ResolveChatUuidAsync(chatId, agentId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved) && !string.Equals(resolved, chatId, StringComparison.OrdinalIgnoreCase))
            {
                return await GetStringAsync(agentId, $"chats/{Uri.EscapeDataString(resolved)}", SlowReadTimeout, ct: cancellationToken);
            }
            throw;
        }
    }

    public Task<ChatHistoryResponseDto?> GetChatHistoryAsync(string chatId, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ChatHistoryResponseDto>("mons", $"chats/{Uri.EscapeDataString(chatId)}", ct: cancellationToken);

    // ------------------------------------------------------------------
    // Capabilities
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<QwenPawSkillDto>> GetSkillsAsync(string? agentId = null, CancellationToken cancellationToken = default)
    {
        var key = FleetInstanceRoutingService.NormalizeAgentId(agentId);
        var lastGoodKey = $"agent360:skills:{key}:last-good";
        try
        {
            return await CachedAsync($"agent360:skills:{key}", TimeSpan.FromSeconds(30), async () =>
            {
                var skills = await GetJsonAsync<IReadOnlyList<QwenPawSkillDto>>(key, "skills", timeout: SlowReadTimeout, ct: cancellationToken) ?? Array.Empty<QwenPawSkillDto>();
                _cache.Set(lastGoodKey, skills, TimeSpan.FromMinutes(30));
                return skills;
            });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException
            && _cache.TryGetValue(lastGoodKey, out IReadOnlyList<QwenPawSkillDto>? _))
        {
            _logger.LogWarning(ex, "Serving last-good skills for {Agent}; runtime read failed", key);
            return _cache.Get<IReadOnlyList<QwenPawSkillDto>>(lastGoodKey)!;
        }
    }

    // ------------------------------------------------------------------
    // Approvals (global router, resolved by session tree)
    // ------------------------------------------------------------------

    private static readonly TimeSpan ApprovalsCacheTtl = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Fleet-wide pending approvals. Every open approvals SSE stream and page poll shares one cached snapshot
    /// (TTL 3 s, single-flight per session key), so N browser tabs cost one fan-out instead of N x instances.
    /// A runtime that times out is backed off for 20 s and its last good list is served meanwhile, instead of
    /// stacking a timeout on an already overloaded node every poll.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ApprovalGates = new();

    public async Task<ApprovalListResponseDto?> GetApprovalsAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var key = $"agent360:approvals:{sessionId ?? "*"}";
        if (_cache.TryGetValue(key, out ApprovalListResponseDto? fresh) && fresh is not null)
        {
            return fresh;
        }
        var gate = ApprovalGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out fresh) && fresh is not null)
            {
                return fresh;
            }
            var result = await LoadApprovalsAsync(sessionId, cancellationToken);
            _cache.Set(key, result, ApprovalsCacheTtl);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ApprovalListResponseDto> LoadApprovalsAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(sessionId)
            ? "approval/list"
            : $"approval/list?session_id={Uri.EscapeDataString(sessionId)}";

        var lists = await Task.WhenAll(_routingService.Instances.Select(async instance =>
        {
            var lastGoodKey = $"agent360:approvals:{instance.Key}:{sessionId ?? "*"}:last-good";
            var backoffKey = $"agent360:approvals:{instance.Key}:backoff";
            if (_cache.TryGetValue(backoffKey, out bool _))
            {
                return _cache.TryGetValue(lastGoodKey, out ApprovalListResponseDto? held) ? held : null;
            }
            try
            {
                using var response = await _routingService.SendAsync(instance, HttpMethod.Get, url, timeout: TimeSpan.FromSeconds(4), ct: cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                var list = await response.Content.ReadFromJsonAsync<ApprovalListResponseDto>(cancellationToken);
                _cache.Remove(backoffKey);
                if (list is not null)
                {
                    _cache.Set(lastGoodKey, list, TimeSpan.FromMinutes(2));
                }
                return list;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _cache.Set(backoffKey, true, TimeSpan.FromSeconds(20));
                _logger.LogWarning("Approvals unavailable on {Instance} ({Error}); backing off 20s", instance.Key, ex.Message);
                return _cache.TryGetValue(lastGoodKey, out ApprovalListResponseDto? stale) ? stale : null;
            }
        }));

        var items = lists.Where(l => l?.PendingApprovals is not null).SelectMany(l => l!.PendingApprovals).ToList();
        return new ApprovalListResponseDto(items, items.Count);
    }

    public Task<ApprovalActionResponseDto?> ApproveToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default) =>
        ResolveApprovalAsync("approve", request, cancellationToken);

    public Task<ApprovalActionResponseDto?> DenyToolAsync(ApprovalActionRequestDto request, CancellationToken cancellationToken = default) =>
        ResolveApprovalAsync("deny", request, cancellationToken);

    private async Task<ApprovalActionResponseDto?> ResolveApprovalAsync(string action, ApprovalActionRequestDto request, CancellationToken ct)
    {
        _cache.Remove("agent360:approvals:*");
        _cache.Remove($"agent360:approvals:{request.SessionId}");
        // The request id only exists on one runtime; the others answer 404.
        foreach (var instance in _routingService.Instances)
        {
            using var response = await _routingService.SendAsync(instance, HttpMethod.Post, $"approval/{action}", () => JsonContent.Create(request), ct: ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ApprovalActionResponseDto>(ct);
        }
        throw new HttpRequestException($"Approval request '{request.RequestId}' not found on any runtime", null, HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------
    // Console
    // ------------------------------------------------------------------

    public async Task<HttpResponseMessage> StreamConsoleChatAsync(ConsoleChatSendRequestDto request, CancellationToken cancellationToken = default)
    {
        var textContent = request.Message;
        if (string.IsNullOrWhiteSpace(textContent) && request.Input is { Count: > 0 })
        {
            textContent = request.Input[0].Content?.FirstOrDefault()?.Text ?? "";
        }

        var sess = request.SessionId ?? "";
        var effectiveChannel = !string.IsNullOrWhiteSpace(request.Channel)
            ? request.Channel
            : sess.StartsWith("telegram:", StringComparison.OrdinalIgnoreCase) || sess.Contains("telegram", StringComparison.OrdinalIgnoreCase)
                ? "telegram"
                : sess.StartsWith("zalo:", StringComparison.OrdinalIgnoreCase) || sess.Contains("zalo", StringComparison.OrdinalIgnoreCase)
                    ? "zalo"
                    : sess.StartsWith("discord:", StringComparison.OrdinalIgnoreCase)
                        ? "discord"
                        : sess.StartsWith("slack:", StringComparison.OrdinalIgnoreCase)
                            ? "slack"
                            : "console";

        var payload = new
        {
            user_id = !string.IsNullOrWhiteSpace(request.UserId) ? request.UserId : "default",
            session_id = !string.IsNullOrWhiteSpace(request.SessionId) ? request.SessionId : $"session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            channel = effectiveChannel,
            loop_mode = request.LoopMode ?? "default",
            input = new[]
            {
                new { role = "user", type = "message", content = new[] { new { type = "text", text = textContent ?? "" } } }
            }
        };

        InvalidateChatList(request.AgentId);
        return await SendAsync(
            Resolve(request.AgentId),
            HttpMethod.Post,
            "console/chat",
            () => JsonContent.Create(payload),
            timeout: TimeSpan.FromMinutes(10),
            completion: HttpCompletionOption.ResponseHeadersRead,
            ct: cancellationToken);
    }

    public async Task<bool> StopChatAsync(ConsoleChatStopRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                Resolve(request.AgentId),
                HttpMethod.Post,
                $"console/chat/stop?chat_id={Uri.EscapeDataString(request.SessionId)}",
                ct: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return doc.RootElement.TryGetProperty("stopped", out var prop) && prop.GetBoolean();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to stop chat for session {SessionId}", request.SessionId);
            return false;
        }
    }

    /// <summary>Attaches to the live turn of any channel session (Telegram, Zalo, cron, console). QwenPaw replays
    /// the current turn from its first event and then streams it live; an idle session returns an empty body.</summary>
    public Task<HttpResponseMessage> ReconnectConsoleChatAsync(string? agentId, string sessionId, string? userId, string? channel, CancellationToken cancellationToken = default)
    {
        var effectiveChannel = !string.IsNullOrWhiteSpace(channel)
            ? channel
            : sessionId.StartsWith("telegram:", StringComparison.OrdinalIgnoreCase) || sessionId.Contains("telegram", StringComparison.OrdinalIgnoreCase)
                ? "telegram"
                : sessionId.StartsWith("zalo:", StringComparison.OrdinalIgnoreCase) || sessionId.Contains("zalo", StringComparison.OrdinalIgnoreCase)
                    ? "zalo"
                    : sessionId.StartsWith("discord:", StringComparison.OrdinalIgnoreCase)
                        ? "discord"
                        : sessionId.StartsWith("slack:", StringComparison.OrdinalIgnoreCase)
                            ? "slack"
                            : "console";

        var effectiveUserId = !string.IsNullOrWhiteSpace(userId)
            ? userId
            : (effectiveChannel is "telegram" or "zalo" && sessionId.Contains(':')
                ? sessionId.Split(':', 2)[1]
                : "default");

        return SendAsync(
            Resolve(agentId),
            HttpMethod.Post,
            "console/chat",
            () => JsonContent.Create(new { reconnect = true, session_id = sessionId, user_id = effectiveUserId, channel = effectiveChannel }),
            timeout: TimeSpan.FromMinutes(30),
            completion: HttpCompletionOption.ResponseHeadersRead,
            ct: cancellationToken);
    }

    // ------------------------------------------------------------------
    // Workspace & Library Artifacts
    // ------------------------------------------------------------------

    public async Task<QwenPawWorkspaceTreeResponse?> GetWorkspaceTreeAsync(
        string? agentId = null,
        CancellationToken cancellationToken = default,
        string? category = null,
        string? ext = null,
        string? search = null,
        bool includeSystem = false)
    {
        var target = Resolve(agentId);
        List<QwenPawWorkspaceEntryDto> rawEntries = [];

        // 1. Prefer recursive /workspace/code-files so all nested artifacts (digest/, media/, incoming/, etc.)
        // are discovered and classified into Library formats.
        try
        {
            using var codeFilesResp = await SendAsync(target, HttpMethod.Get, "workspace/code-files", timeout: TimeSpan.FromSeconds(15), ct: cancellationToken);
            if (codeFilesResp.IsSuccessStatusCode)
            {
                var files = await codeFilesResp.Content.ReadFromJsonAsync<JsonArray>(cancellationToken) ?? [];
                rawEntries = files
                    .Where(f => f is not null)
                    .Select(f => new QwenPawWorkspaceEntryDto(
                        f!["filename"]?.GetValue<string>() ?? f["path"]?.GetValue<string>() ?? "file",
                        f["path"]?.GetValue<string>() ?? "",
                        "file",
                        null,
                        f["size"]?.GetValue<long>(),
                        f["modified_time"]?.GetValue<string>()))
                    .ToList();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "workspace/code-files fallback to workspace/tree for {AgentId}", agentId);
        }

        // 2. Fallback to /workspace/tree if code-files returned empty or was unavailable
        if (rawEntries.Count == 0)
        {
            using var response = await SendAsync(target, HttpMethod.Get, "workspace/tree", ct: cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var treeResp = await response.Content.ReadFromJsonAsync<QwenPawWorkspaceTreeResponse>(cancellationToken);
                if (treeResp?.Entries is not null)
                {
                    rawEntries = treeResp.Entries.ToList();
                }
            }
        }

        // 3. Filter & enrich into user-facing Library artifacts
        var enrichedAll = rawEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Where(e => includeSystem ? Agent360.API.Features.Agents.Security.WorkspacePathAcl.IsPathAllowed(e.Path) : Agent360.API.Features.Agents.Security.WorkspacePathAcl.IsLibraryArtifactPath(e.Path))
            .Select(Agent360.API.Features.Agents.Security.WorkspacePathAcl.EnrichEntry)
            .OrderByDescending(e => e.ModifiedAt ?? "")
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 4. Compute facet counts across all valid artifacts for this agent
        var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["all"] = enrichedAll.Count,
            ["pages"] = 0,
            ["files"] = 0,
            ["apps"] = 0,
            ["images"] = 0,
            ["media"] = 0
        };
        var formatCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in enrichedAll)
        {
            var cat = item.Category ?? "files";
            categoryCounts[cat] = categoryCounts.TryGetValue(cat, out var c) ? c + 1 : 1;

            if (!string.IsNullOrWhiteSpace(item.Extension))
            {
                var formatKey = item.Extension.ToLowerInvariant();
                formatCounts[formatKey] = formatCounts.TryGetValue(formatKey, out var fc) ? fc + 1 : 1;
            }
        }

        // 5. Apply optional query filters (category, extension, search)
        IEnumerable<QwenPawWorkspaceEntryDto> filtered = enrichedAll;

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var wantedCat = category.Trim().ToLowerInvariant();
            filtered = filtered.Where(e => string.Equals(e.Category, wantedCat, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(ext))
        {
            var wantedExts = ext.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.TrimStart('.').ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wantedExts.Count > 0)
            {
                filtered = filtered.Where(e => e.Extension is not null && wantedExts.Contains(e.Extension));
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            filtered = filtered.Where(e =>
                (e.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Path?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Extension?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var finalList = filtered.ToList();
        return new QwenPawWorkspaceTreeResponse(
            Directory: "",
            Entries: finalList,
            HasMore: false,
            NextCursor: null,
            CategoryCounts: categoryCounts,
            FormatCounts: formatCounts,
            TotalCount: enrichedAll.Count);
    }

    public async Task<HttpResponseMessage> DownloadWorkspaceFileAsync(string path, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var target = Resolve(agentId);
        var response = await SendAsync(
            target,
            HttpMethod.Get,
            $"workspace/file-download?path={Uri.EscapeDataString(path)}",
            timeout: TimeSpan.FromMinutes(2),
            completion: HttpCompletionOption.ResponseHeadersRead,
            ct: cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            response.Dispose();
            var encoded = string.Join('/', path.Trim('/').Split('/').Select(Uri.EscapeDataString));
            return await SendAsync(
                target,
                HttpMethod.Get,
                $"workspace/binary-files/{encoded}",
                timeout: TimeSpan.FromMinutes(2),
                completion: HttpCompletionOption.ResponseHeadersRead,
                ct: cancellationToken);
        }

        return response;
    }

    public async Task<string?> GetFileContentAsync(string path, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var target = Resolve(agentId);
        using var response = await SendAsync(target, HttpMethod.Get, $"workspace/file-content?path={Uri.EscapeDataString(path)}", ct: cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            var encoded = string.Join('/', path.Trim('/').Split('/').Select(Uri.EscapeDataString));
            using var legacy = await SendAsync(target, HttpMethod.Get, $"workspace/code-files/{encoded}", ct: cancellationToken);
            if (legacy.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            legacy.EnsureSuccessStatusCode();
            var legacyResult = await legacy.Content.ReadFromJsonAsync<WorkspaceFileContentDto>(cancellationToken);
            return legacyResult?.Content;
        }
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<WorkspaceFileContentDto>(cancellationToken);
        return result?.Content;
    }

    // ------------------------------------------------------------------
    // Context compaction (native checkpoint GC + /compact)
    // ------------------------------------------------------------------

    public async Task<CompactContextResponseDto> CompactContextAsync(CompactContextRequestDto request, CancellationToken cancellationToken = default)
    {
        var targetAgent = FleetInstanceRoutingService.NormalizeAgentId(request.AgentId);
        var target = Resolve(targetAgent);

        try
        {
            using var gcResp = await SendAsync(target, HttpMethod.Post, "workspace/checkpoints/gc", () => JsonContent.Create(new { compact = true }), timeout: TimeSpan.FromSeconds(30), ct: cancellationToken);
            gcResp.EnsureSuccessStatusCode();
            var gcDetails = await gcResp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                var compactMsg = string.IsNullOrWhiteSpace(request.Hint) ? "/compact" : $"/compact {request.Hint.Trim()}";
                try
                {
                    using var chatResp = await StreamConsoleChatAsync(new ConsoleChatSendRequestDto(request.SessionId, compactMsg, targetAgent, null, null), cancellationToken);
                    await chatResp.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (Exception chatEx) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(chatEx, "Non-blocking: /compact failed for session {SessionId}", request.SessionId);
                }
            }

            return new CompactContextResponseDto(true, targetAgent, request.SessionId, "native_compaction_and_gc", gcDetails);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to execute context compaction on agent {AgentId}", targetAgent);
            return new CompactContextResponseDto(false, targetAgent, request.SessionId, "error", new { error = ex.Message });
        }
    }

    // ------------------------------------------------------------------
    // Tool call receipts
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ToolCallReceiptContractDto>> GetToolCallReceiptsAsync(string sessionId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var targetAgent = FleetInstanceRoutingService.NormalizeAgentId(agentId);
        var target = _routingService.ResolveTarget(targetAgent);
        if (!target.IsQwenPaw)
        {
            return Array.Empty<ToolCallReceiptContractDto>();
        }

        try
        {
            using var resp = await SendAsync(target, HttpMethod.Get, $"tool-calls/{Uri.EscapeDataString(sessionId)}", global: true, ct: cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return Array.Empty<ToolCallReceiptContractDto>();
            }

            var node = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var items = node?["items"]?.AsArray();
            if (items == null || items.Count == 0)
            {
                return Array.Empty<ToolCallReceiptContractDto>();
            }

            var list = new List<ToolCallReceiptContractDto>();
            foreach (var item in items)
            {
                if (item is null) continue;
                var toolCallId = item["tool_call_id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                var toolName = item["tool_name"]?.ToString() ?? "unknown_tool";
                var statusStr = item["status"]?.ToString()?.ToLowerInvariant() ?? "running";

                var normalizedStatus = statusStr switch
                {
                    "completed" => "succeeded",
                    "success" => "succeeded",
                    "failed" => "failed",
                    "error" => "failed",
                    "cancelled" => "cancelled",
                    "awaiting_approval" => "awaiting_approval",
                    "denied" => "denied",
                    _ => "running"
                };

                // Compute deterministic policy and auth hashes
                var policyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{targetAgent}:{toolName}:v1"))).ToLowerInvariant();
                var authDecisionId = $"auth-dec-{policyHash.Substring(0, 12)}";

                list.Add(new ToolCallReceiptContractDto(
                    Schema: "agent360/tool-call-receipt/v1",
                    ToolCallId: toolCallId,
                    BotId: $"bot:agent360:{targetAgent}",
                    BotBindingId: $"binding:{targetAgent}",
                    DeploymentId: $"deploy:{targetAgent}:default",
                    RunId: sessionId,
                    CapabilityId: toolName,
                    InputHash: null,
                    AuthorizationDecisionId: authDecisionId,
                    PolicyHash: policyHash,
                    Status: normalizedStatus,
                    OutputHash: null,
                    Error: item["end_state"]?.ToString()));
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tool calls receipts for session {SessionId}", sessionId);
            return Array.Empty<ToolCallReceiptContractDto>();
        }
    }


    // ------------------------------------------------------------------
    // Analytics (aggregated per runtime; Core is the default analytics source)
    // ------------------------------------------------------------------

    public Task<AgentStatsSummaryDto?> GetAgentStatsAsync(string? startDate = null, string? endDate = null, CancellationToken cancellationToken = default) =>
        GetJsonAsync<AgentStatsSummaryDto>("mons", "agent-stats" + Query(startDate, endDate), global: true, ct: cancellationToken);

    public Task<IReadOnlyList<LlmToolDailyDto>?> GetGlobalLlmToolTrendAsync(string? startDate = null, string? endDate = null, CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<LlmToolDailyDto>>("mons", "agent-stats/llm-tool-trend" + Query(startDate, endDate), global: true, ct: cancellationToken);

    public async Task<TokenUsageResponseDto?> GetTokenUsageAsync(CancellationToken cancellationToken = default) =>
        await CachedAsync("agent360:token-usage", TimeSpan.FromSeconds(30),
            () => GetJsonAsync<TokenUsageResponseDto>("mons", "token-usage", global: true, ct: cancellationToken));

    private static string Query(string? startDate, string? endDate)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(startDate)) parts.Add($"start_date={Uri.EscapeDataString(startDate)}");
        if (!string.IsNullOrWhiteSpace(endDate)) parts.Add($"end_date={Uri.EscapeDataString(endDate)}");
        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }

    // ------------------------------------------------------------------
    // Presentation mapping
    // ------------------------------------------------------------------

    private static readonly IReadOnlyList<AgentToolSummary> DefaultTools =
    [
        new("bash", "builtin", "Execute non-mutating shell commands safely"),
        new("web_search", "builtin", "Perform internet and documentation retrieval")
    ];

    private static (string FriendlyName, string RoleTag) MapAgentIdentity(string agentId, string? description, string? runtime)
    {
        var id = agentId.ToLowerInvariant();
        if (id == "mons") return ("Mons (Core Dev & Ops)", "Engineering");
        if (id == "ken") return ("Ken (Product Owner)", "Operations");
        if (id == "zen") return ("Zen (Sales & Delivery)", "Operations");
        if (id == "mkai") return ("Mkai (Generalist Core)", "Engineering");
        if (id == "default-core") return ("Core Skyer Default", "Operations");
        if (id.Contains("qa_agent")) return ("QA Helper (Core)", "Quality");
        if (id == "gm-boc") return ("GM Bốc (Executive)", "Operations");
        if (id == "gm-gao") return ("GM Gạo (Procurement)", "Operations");
        if (id == "gm-lua") return ("GM Lúa (Customer Support)", "Operations");
        if (id == "gm-team") return ("GM Team Ops", "Operations");
        if (id == "climax-default") return ("Climax Default (Creative)", "Research");
        if (id == "climax-team") return ("Climax Team (Scraper & Ops)", "Operations");
        if (id == "planthy-pm") return ("Planthy PM (Delivery Office)", "Operations");
        if (id == "planthy-team") return ("Planthy Team (Execution)", "Engineering");
        if (id == "agentteams-manager") return ("AgentTeams Manager Swarm", "Engineering");
        if (id == "agentteams-gateway") return ("AgentTeams AI Gateway", "Engineering");
        if (id == "llm360-inference-provider") return ("LLM360 Inference Gateway", "Security");
        if (id == "tsx-copaw") return ("TSX CoPaw Automation", "Operations");
        if (id == "its-default" || id == "its") return ("ITS Gatekeeper (Inox Tân Sơn)", "Operations");
        if (id == "gl-default" || id == "gl") return ("GL Automation & Analytics", "Engineering");

        var roleTag = DetermineRoleTag(agentId, agentId, description);
        return (agentId, roleTag);
    }

    private static string ExtractModel(AgentGuardianAgentDto g)
    {
        if (g.ModelValidation?.Model is not null)
        {
            var provider = g.ModelValidation.ProviderId;
            return !string.IsNullOrWhiteSpace(provider) ? $"{provider}/{g.ModelValidation.Model}" : g.ModelValidation.Model;
        }

        if (g.Stages != null && g.Stages.TryGetValue("capability", out var cap) && !string.IsNullOrWhiteSpace(cap) && cap != "endpoint_ok")
        {
            return cap;
        }

        return "grok-4.7";
    }

    private static IReadOnlyList<AgentToolSummary> ExtractTools(AgentGuardianAgentDto g)
    {
        var tools = new List<AgentToolSummary>();
        if (g.Mcp?.Clients is { Count: > 0 })
        {
            foreach (var client in g.Mcp.Clients)
            {
                if (client.ToolNames is { Count: > 0 })
                {
                    foreach (var toolName in client.ToolNames)
                    {
                        tools.Add(new AgentToolSummary(toolName, "mcp", client.Description ?? $"{client.Name} MCP Tool"));
                    }
                }
            }
        }

        if (tools.Count == 0)
        {
            tools.Add(new AgentToolSummary("bash", "builtin", "Execute non-mutating shell commands safely"));
            tools.Add(new AgentToolSummary("web_search", "builtin", "Perform internet and documentation retrieval"));
        }

        return tools;
    }

    private static string FormatNodeDisplay(string? node, string? instance)
    {
        var cleanNode = (node ?? "Infra").Trim();
        if (instance != null)
        {
            if (instance.Contains("Core")) return "Node 3rd (Core) - ai.hmz.one";
            if (instance.Contains("Gia Moc")) return "Node 6th (Gia Mộc) - ai-gm.antares.vn";
            if (instance.Contains("Climax")) return "Node 5th (Climax) - ai-climax.antares.vn";
            if (instance.Contains("Planthy")) return "Node 5th (Planthy) - planthy-ai.hmz.one";
            if (instance.Contains("AgentTeams")) return "Node 4th (AgentTeams) - manager-ai.hmz.one";
            if (instance.Contains("LLM360")) return "Node 4th (LLM360) - proxy-llm360.hmz.one";
            if (instance.Contains("TSX")) return "Node 4th (TSX) - ai-tsx.hmz.one";
            if (instance.Contains("ITS")) return "Node 8th (ITS) - ai-its.antares.vn";
            if (instance.Contains("GL")) return "Node 8th (GL) - ai-gl.antares.vn";
        }

        return $"Node {cleanNode} - {instance ?? "Infra"}";
    }

    private static string DetermineRoleTag(string id, string name, string? desc)
    {
        var text = $"{id} {name} {desc}".ToLowerInvariant();
        if (text.Contains("qa") || text.Contains("quality") || text.Contains("test"))
        {
            return "Quality";
        }
        if (text.Contains("ops") || text.Contains("sre") || text.Contains("workflow") || text.Contains("manage") || text.Contains("support") || text.Contains("po") || text.Contains("sales"))
        {
            return "Operations";
        }
        if (text.Contains("creative") || text.Contains("research") || text.Contains("market") || text.Contains("content"))
        {
            return "Research";
        }
        if (text.Contains("security") || text.Contains("proxy") || text.Contains("iam") || text.Contains("gateway"))
        {
            return "Security";
        }
        return "Engineering";
    }

}
