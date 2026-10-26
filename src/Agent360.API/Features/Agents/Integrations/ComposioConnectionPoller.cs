using System.Collections.Concurrent;
using System.Text.Json;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.Integrations;

public interface IComposioConnectionPoller
{
    void TrackPendingConnection(string agentId, string appSlug);
    ComposioConnectionPollStatus? GetStatus(string agentId, string appSlug);
}

public sealed record ComposioConnectionPollStatus(
    string AgentId,
    string AppSlug,
    string Status, // "initiated", "active", "failed", "timeout"
    DateTime StartedAt,
    DateTime LastCheckedAt,
    string? Message = null);

public sealed class ComposioConnectionPoller : BackgroundService, IComposioConnectionPoller
{
    private const string ComposioMcpHubUrl = "https://connect.composio.dev/mcp";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxTrackingDuration = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly IFleetInstanceRoutingService _routing;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ComposioConnectionPoller> _logger;

    private readonly ConcurrentDictionary<string, ComposioConnectionPollStatus> _tracked = new(StringComparer.OrdinalIgnoreCase);

    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Options.ComposioOptions> _composioOptions;

    public ComposioConnectionPoller(
        HttpClient httpClient,
        IFleetInstanceRoutingService routing,
        IMemoryCache cache,
        Microsoft.Extensions.Options.IOptionsMonitor<Agent360.API.Options.ComposioOptions> composioOptions,
        ILogger<ComposioConnectionPoller> logger)
    {
        _httpClient = httpClient;
        _routing = routing;
        _cache = cache;
        _composioOptions = composioOptions;
        _logger = logger;
    }

    private static string Key(string agentId, string appSlug) =>
        $"{agentId.Trim().ToLowerInvariant()}:{appSlug.Trim().ToLowerInvariant()}";

    public void TrackPendingConnection(string agentId, string appSlug)
    {
        var cleanAgent = agentId.Replace("bot:agent360:", "").Trim().ToLowerInvariant();
        var cleanSlug = appSlug.Trim().ToLowerInvariant();
        var k = Key(cleanAgent, cleanSlug);

        _tracked[k] = new ComposioConnectionPollStatus(
            cleanAgent,
            cleanSlug,
            "initiated",
            DateTime.UtcNow,
            DateTime.UtcNow,
            "Awaiting user authorization in browser...");

        _logger.LogInformation("[ComposioPoller] Began tracking OAuth for {Agent} on app {App}", cleanAgent, cleanSlug);
    }

    public ComposioConnectionPollStatus? GetStatus(string agentId, string appSlug)
    {
        var cleanAgent = agentId.Replace("bot:agent360:", "").Trim().ToLowerInvariant();
        var cleanSlug = appSlug.Trim().ToLowerInvariant();
        var k = Key(cleanAgent, cleanSlug);

        if (_tracked.TryGetValue(k, out var status))
        {
            return status;
        }

        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_tracked.IsEmpty)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                foreach (var (k, current) in _tracked.ToArray())
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    // If terminal or expired, clean up after retaining for 2 minutes
                    if (current.Status is "active" or "failed" or "timeout")
                    {
                        if (DateTime.UtcNow - current.LastCheckedAt > TimeSpan.FromMinutes(2))
                        {
                            _tracked.TryRemove(k, out _);
                        }
                        continue;
                    }

                    if (DateTime.UtcNow - current.StartedAt > MaxTrackingDuration)
                    {
                        _tracked[k] = current with
                        {
                            Status = "timeout",
                            LastCheckedAt = DateTime.UtcNow,
                            Message = "OAuth session expired after 10 minutes without authorization."
                        };
                        continue;
                    }

                    await PollSingleConnectionAsync(k, current, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[ComposioPoller] Error in background polling cycle");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollSingleConnectionAsync(string key, ComposioConnectionPollStatus current, CancellationToken ct)
    {
        var effectiveToken = InfisicalCredentialAuditService.ResolveAgentComposioToken(current.AgentId, allowWorkspaceFallback: true);
        if (string.IsNullOrWhiteSpace(effectiveToken))
        {
            _tracked[key] = current with
            {
                Status = "failed",
                LastCheckedAt = DateTime.UtcNow,
                Message = "No valid Composio API key resolved for agent."
            };
            return;
        }

        try
        {
            var serialized = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "tools/call",
                @params = new
                {
                    name = "COMPOSIO_MANAGE_CONNECTIONS",
                    arguments = new { toolkits = new[] { current.AppSlug } }
                },
                id = 1
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, ComposioMcpHubUrl)
            {
                Content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", $"Bearer {effectiveToken}");
            req.Headers.Add("Accept", "application/json, text/event-stream");

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var rawStream = await resp.Content.ReadAsStringAsync(ct);
            foreach (var line in rawStream.Split((char)10, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("data: "))
                {
                    var dataStr = line.Substring(6).Trim();
                    using var doc = JsonDocument.Parse(dataStr);
                    if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                        resultEl.TryGetProperty("content", out var contentArray) &&
                        contentArray.GetArrayLength() > 0)
                    {
                        var textJson = contentArray[0].GetProperty("text").GetString();
                        if (!string.IsNullOrWhiteSpace(textJson))
                        {
                            using var innerDoc = JsonDocument.Parse(textJson);
                            if (innerDoc.RootElement.TryGetProperty("data", out var dataEl) &&
                                dataEl.TryGetProperty("results", out var resultsEl) &&
                                resultsEl.TryGetProperty(current.AppSlug, out var appResEl))
                            {
                                var statusStr = appResEl.TryGetProperty("status", out var s) ? s.GetString() : "initiated";
                                var accounts = appResEl.TryGetProperty("accounts", out var a) && a.ValueKind == JsonValueKind.Array
                                    ? a.EnumerateArray()
                                    : Enumerable.Empty<JsonElement>();

                                var isNowActive = string.Equals(statusStr, "active", StringComparison.OrdinalIgnoreCase) ||
                                                  accounts.Any(acc => string.Equals(acc.TryGetProperty("status", out var st) ? st.GetString() : "", "active", StringComparison.OrdinalIgnoreCase));

                                if (isNowActive)
                                {
                                    _tracked[key] = current with
                                    {
                                        Status = "active",
                                        LastCheckedAt = DateTime.UtcNow,
                                        Message = $"Connection authorized successfully. Active credentials bound to {current.AppSlug}."
                                    };

                                    _logger.LogInformation("[ComposioPoller] SUCCESS: {Agent} connected {App}!", current.AgentId, current.AppSlug);

                                    // Invalidate agent MCP cache so frontend immediately sees active tools
                                    var cacheKey = Agent360.API.Features.Agents.Endpoints.AgentRuntimeProxyEndpoint.CacheKey(current.AgentId, "mcp");
                                    _cache.Remove(cacheKey);
                                    _cache.Remove(cacheKey + ":last-good");
                                }
                                else
                                {
                                    _tracked[key] = current with
                                    {
                                        LastCheckedAt = DateTime.UtcNow,
                                        Message = "Waiting for user to complete OAuth in browser..."
                                    };
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("[ComposioPoller] Probe exception for {Key}: {Err}", key, ex.Message);
        }
    }
}
