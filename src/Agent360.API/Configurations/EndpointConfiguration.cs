using Agent360.API.Features.Agents.Services;
using Agent360.API.Features.Agents.SubAgents;
using Agent360.API.Filters;

namespace Agent360.API.Configurations;

public static class EndpointConfiguration
{
    extension(WebApplicationBuilder webAppBuilder)
    {
        public WebApplicationBuilder ConfigureEndpoints()
        {
            webAppBuilder.Services.AddScoped<LogtoWebhookSignatureFilter>();
            webAppBuilder.Services.AddSingleton<SessionQueueManager>();
            webAppBuilder.Services.AddMemoryCache();
            webAppBuilder.Services.AddSingleton<FleetRoutingState>();
#pragma warning disable EXTEXP0001 // Fleet calls stream long chat turns; hidden retries would duplicate POSTs and a shared breaker would cut every agent.
            webAppBuilder.Services.AddHttpClient<IFleetInstanceRoutingService, FleetInstanceRoutingService>()
                .RemoveAllResilienceHandlers()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    // Tailnet connects take ms; a dead internal route must fail fast so failover is cheap.
                    ConnectTimeout = TimeSpan.FromSeconds(3),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
                    MaxConnectionsPerServer = 64,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                });
#pragma warning restore EXTEXP0001
            webAppBuilder.Services.AddHttpClient<IQwenPawClient, QwenPawClient>();
            // Internal (tailnet) CDP only: fast connect failure, pooled sockets, no proxies, no redirects.
            webAppBuilder.Services.AddHttpClient(Agent360.API.Features.Agents.Browser.AgentBrowserDirectory.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(8))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                    MaxConnectionsPerServer = 16,
                    UseProxy = false,
                    AllowAutoRedirect = false,
                });
            webAppBuilder.Services.AddSingleton<Agent360.API.Features.Agents.Browser.AgentBrowserGate>();
            // Scoped: depends on the transient typed fleet client; its own cache is the shared IMemoryCache.
            webAppBuilder.Services.AddScoped<Agent360.API.Features.Agents.Browser.AgentBrowserDirectory>();
            webAppBuilder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                // Per client IP: preview polling (1 req/5 s) and focus/pages calls fit far below this.
                o.AddPolicy(Agent360.API.Features.Agents.Browser.AgentBrowserCdpEndpoint.RateLimitPolicy, http =>
                    System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                        http.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                            ?? http.Request.Headers["X-Real-IP"].FirstOrDefault()
                            ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
            webAppBuilder.Services.AddOptions<SubAgentOptions>().BindConfiguration(SubAgentOptions.Section);
            webAppBuilder.Services.AddSingleton<SubAgentStore>();
            webAppBuilder.Services.AddScoped<SubAgentService>();
            webAppBuilder.Services.AddSingleton<SubAgentSkillSyncQueue>();
            webAppBuilder.Services.AddSingleton<ISubAgentSkillSyncQueue>(sp => sp.GetRequiredService<SubAgentSkillSyncQueue>());
            webAppBuilder.Services.AddHostedService(sp => sp.GetRequiredService<SubAgentSkillSyncQueue>());
            webAppBuilder.Services.AddOptions<Agent360SecurityOptions>().BindConfiguration(Agent360SecurityOptions.Section);
            webAppBuilder.Services.AddScoped<OperatorAuthFilter>();
            webAppBuilder.Services.AddSingleton<DelegationMonitor>();
            webAppBuilder.Services.AddHostedService(sp => sp.GetRequiredService<DelegationMonitor>());
            webAppBuilder.Services.AddHostedService<AgentCacheWarmer>();
            // Inter-agent turns can legitimately take minutes; per-call timeouts are enforced in the adapter.
#pragma warning disable EXTEXP0001 // Long inter-agent turns must not be cut by the default 30s resilience timeout.
            webAppBuilder.Services.AddHttpClient<IQwenPawA2AClient, QwenPawA2AClient>()
                .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
            return webAppBuilder;
        }
    }
}
