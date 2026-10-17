using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Agent360.API.Options;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Services;

public interface IFleetInstanceRoutingService
{
    ResolvedInstanceTarget ResolveTarget(string? agentId);
    Task<HttpRequestMessage> CreateProxiedRequestAsync(HttpMethod method, string path, string? agentId, HttpContent? content, CancellationToken ct = default);
}

public sealed record ResolvedInstanceTarget(
    string ClusterNode,
    string InstanceName,
    string BaseUrl,
    string TargetAgentProfile,
    string? LoginPath,
    string? Username,
    string? Password,
    string CdpUrl);

public sealed class FleetInstanceRoutingService : IFleetInstanceRoutingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FleetInstanceRoutingService> _logger;
    private readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> _tokenCache = new();
    private readonly QwenPawOptions _options;

    public FleetInstanceRoutingService(HttpClient httpClient, ILogger<FleetInstanceRoutingService> logger, IOptions<QwenPawOptions>? options = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options?.Value ?? new QwenPawOptions();
    }

    // Instance credentials are injected from configuration (Infisical-rendered env:
    // QwenPaw__Instances__<key>__Username / __Password); never hardcode them in source.
    private (string? User, string? Password) Credentials(string key)
    {
        if (_options.Instances.TryGetValue(key, out var inst) && !string.IsNullOrWhiteSpace(inst.Password))
        {
            return (string.IsNullOrWhiteSpace(inst.Username) ? "admin" : inst.Username, inst.Password);
        }
        _logger.LogWarning("No QwenPaw credentials configured for instance {Instance}; requests will be unauthenticated", key);
        return (null, null);
    }

    public ResolvedInstanceTarget ResolveTarget(string? agentId)
    {
        var raw = (agentId ?? "mons").Replace("bot:agent360:", "").Trim();
        var id = raw.ToLowerInvariant();

        // 1. Gia Moc Node 6th (Dedicated Ops & ERP)
        if (id.StartsWith("gm-") || id.Contains("giamoc") || id.Contains("boc") || id.Contains("gao") || id.Contains("lua"))
        {
            var profile = id switch
            {
                "gm-boc" => "default",
                "gm-team" => "team",
                "gm-lua" => "customer",
                "gm-gao" => "supplier",
                _ => raw.Replace("gm-", "")
            };
            return new ResolvedInstanceTarget(
                ClusterNode: "6th",
                InstanceName: "Gia Moc QwenPaw (Node 6th)",
                BaseUrl: "https://ai-gm.antares.vn",
                TargetAgentProfile: profile,
                LoginPath: "/api/auth/login",
                Username: Credentials("gm").User,
                Password: Credentials("gm").Password,
                CdpUrl: "http://100.81.66.42:19222");
        }

        // 2. Climax Creative Node 5th (Content & Discovery)
        if (id.StartsWith("climax-"))
        {
            var profile = id == "climax-team" ? "team" : "default";
            return new ResolvedInstanceTarget(
                ClusterNode: "5th",
                InstanceName: "Climax Creative (Node 5th)",
                BaseUrl: "https://ai-climax.antares.vn",
                TargetAgentProfile: profile,
                LoginPath: "/api/auth/login",
                Username: Credentials("climax").User,
                Password: Credentials("climax").Password,
                CdpUrl: "http://100.70.201.83:19222");
        }

        // 3. Planthy AI Node 5th (Delivery Office)
        if (id.StartsWith("planthy-"))
        {
            var profile = id == "planthy-team" ? "team" : "default";
            return new ResolvedInstanceTarget(
                ClusterNode: "5th",
                InstanceName: "Planthy AI (Node 5th)",
                BaseUrl: "https://planthy-ai.hmz.one",
                TargetAgentProfile: profile,
                LoginPath: "/api/auth/login",
                Username: Credentials("planthy").User,
                Password: Credentials("planthy").Password,
                CdpUrl: "http://100.70.201.83:19222");
        }

        // 4. Default: Core CoPaw / QwenPaw Node 3rd
        var coreProfile = id switch
        {
            "default-core" => "default",
            "mons-qwenpaw_qa_agent_0.2" => "QwenPaw_QA_Agent_0.2",
            _ => raw
        };

        var cdp = id == "mons" ? "http://100.81.66.42:19222" : "http://100.76.185.111:19222";

        return new ResolvedInstanceTarget(
            ClusterNode: "3rd",
            InstanceName: "Core QwenPaw / CoPaw (Node 3rd)",
            BaseUrl: "https://ai.hmz.one",
            TargetAgentProfile: coreProfile,
            LoginPath: null,
            Username: null,
            Password: null,
            CdpUrl: cdp);
    }

    public async Task<HttpRequestMessage> CreateProxiedRequestAsync(
        HttpMethod method,
        string path,
        string? agentId,
        HttpContent? content,
        CancellationToken ct = default)
    {
        var target = ResolveTarget(agentId);
        var baseUri = new Uri(target.BaseUrl.TrimEnd('/') + "/");
        var cleanPath = path.TrimStart('/');
        var fullUri = new Uri(baseUri, cleanPath);

        var request = new HttpRequestMessage(method, fullUri);
        if (content != null)
        {
            request.Content = content;
        }

        request.Headers.TryAddWithoutValidation("X-Agent-Id", target.TargetAgentProfile);

        // Authenticate if instance requires token login
        if (!string.IsNullOrWhiteSpace(target.LoginPath) && !string.IsNullOrWhiteSpace(target.Username) && !string.IsNullOrWhiteSpace(target.Password))
        {
            var token = await GetOrRefreshTokenAsync(target, ct);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return request;
    }

    private async Task<string?> GetOrRefreshTokenAsync(ResolvedInstanceTarget target, CancellationToken ct)
    {
        var cacheKey = $"{target.BaseUrl}:{target.Username}";
        if (_tokenCache.TryGetValue(cacheKey, out var item) && DateTime.UtcNow < item.ExpiresAt)
        {
            return item.Token;
        }

        try
        {
            var loginUri = new Uri(new Uri(target.BaseUrl.TrimEnd('/') + "/"), target.LoginPath!.TrimStart('/'));
            var payload = new { username = target.Username, password = target.Password };

            using var resp = await _httpClient.PostAsJsonAsync(loginUri, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed login to instance {Url}. Status: {Status}", target.BaseUrl, resp.StatusCode);
                return null;
            }

            var node = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            var token = node?["token"]?.ToString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                // Cache token for 6 hours
                _tokenCache[cacheKey] = (token, DateTime.UtcNow.AddHours(6));
                return token;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception authenticating to instance {Url}", target.BaseUrl);
        }

        return null;
    }
}
