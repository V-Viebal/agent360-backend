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
            webAppBuilder.Services.AddScoped<Agent360.API.Features.Agents.Security.SecureUploadStorageService>();
            webAppBuilder.Services.AddHttpClient("MinioStorageClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });
            webAppBuilder.Services.AddSingleton<SessionQueueManager>();
            webAppBuilder.Services.AddSingleton<SessionMultiplexerHub>();
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
            webAppBuilder.Services.AddHttpClient<Agent360.API.Features.Billing.PayOSService>();
            webAppBuilder.Services.AddScoped<Agent360.API.Features.Billing.WalletService>();
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
            webAppBuilder.Services.AddSingleton<Agent360.API.Features.Agents.Browser.ScreencastHub>();
            // Scoped: depends on the transient typed fleet client; its own cache is the shared IMemoryCache.
            webAppBuilder.Services.AddScoped<Agent360.API.Features.Agents.Browser.AgentBrowserDirectory>();
            webAppBuilder.Services.AddSingleton<Agent360.API.Features.Agents.Browser.BrowserViewerAccess>();
            webAppBuilder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.OnRejected = (ctx, _) =>
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = "10";
                    return ValueTask.CompletedTask;
                };
                // Global per-client budget for the (mostly anonymous) API: generous for the SPA's polling, but
                // stops scripted floods. Writes/chat get a tighter bucket than reads; health probes are exempt.
                o.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(http =>
                {
                    var path = http.Request.Path.Value ?? "";
                    // The desktop forward-auth arrives from each browser node's Traefik (one IP for every viewer asset).
                    if (path is "/health" or "/alive" or Agent360.API.Features.Agents.Browser.BrowserViewerAccessEndpoint.VerifyPath || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                        return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("exempt");
                    var ip = http.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                        ?? http.Request.Headers["X-Real-IP"].FirstOrDefault()
                        ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var write = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method);
                    return write
                        ? System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter("w:" + ip, _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
                            { TokenLimit = 60, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromSeconds(10), QueueLimit = 0, AutoReplenishment = true })
                        : System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter("r:" + ip, _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
                            { TokenLimit = 600, TokensPerPeriod = 200, ReplenishmentPeriod = TimeSpan.FromSeconds(10), QueueLimit = 0, AutoReplenishment = true });
                });
                // Per client IP: preview polling (1 req/5 s) and focus/pages calls fit far below this.
                o.AddPolicy(Agent360.API.Features.Agents.Browser.AgentBrowserCdpEndpoint.RateLimitPolicy, http =>
                    System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                        http.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                            ?? http.Request.Headers["X-Real-IP"].FirstOrDefault()
                            ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
            webAppBuilder.Services.AddOptions<SubAgentOptions>().BindConfiguration(SubAgentOptions.Section);
            webAppBuilder.Services.AddSingleton<SubAgentStore>();
            webAppBuilder.Services.AddSingleton<SessionRoomStore>();
            webAppBuilder.Services.AddSingleton<Agent360.API.Features.Agents.Endpoints.AgentLoopModeStore>();
            webAppBuilder.Services.AddScoped<SubAgentService>();
            webAppBuilder.Services.AddSingleton<Agent360.API.Features.Agents.Security.Iam360CellsPersistence>();
            webAppBuilder.Services.AddScoped<Agent360.API.Features.Agents.Security.IIam360CellsService, Agent360.API.Features.Agents.Security.Iam360CellsService>();
            webAppBuilder.Services.AddScoped<SessionRoomOrchestrator>();
            webAppBuilder.Services.AddSingleton<SubAgentSkillSyncQueue>();
            webAppBuilder.Services.AddSingleton<ISubAgentSkillSyncQueue>(sp => sp.GetRequiredService<SubAgentSkillSyncQueue>());
            webAppBuilder.Services.AddHostedService(sp => sp.GetRequiredService<SubAgentSkillSyncQueue>());
            webAppBuilder.Services.AddOptions<Agent360SecurityOptions>().BindConfiguration(Agent360SecurityOptions.Section);
            webAppBuilder.Services.AddScoped<OperatorAuthFilter>();
            webAppBuilder.Services.AddSingleton<DelegationMonitor>();
            webAppBuilder.Services.AddHostedService(sp => sp.GetRequiredService<DelegationMonitor>());
            webAppBuilder.Services.AddHostedService<AgentCacheWarmer>();
            webAppBuilder.Services.AddHostedService<Agent360.API.Features.Agents.Services.SessionSyncBackgroundService>();
            // Inter-agent turns can legitimately take minutes; per-call timeouts are enforced in the adapter.
#pragma warning disable EXTEXP0001 // Long inter-agent turns must not be cut by the default 30s resilience timeout.
            webAppBuilder.Services.AddHttpClient<IQwenPawA2AClient, QwenPawA2AClient>()
                .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
            return webAppBuilder;
        }
    }
}
