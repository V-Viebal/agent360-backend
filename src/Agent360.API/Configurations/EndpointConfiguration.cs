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
            webAppBuilder.Services.AddHttpClient<IFleetInstanceRoutingService, FleetInstanceRoutingService>();
            webAppBuilder.Services.AddHttpClient<IQwenPawClient, QwenPawClient>();
            webAppBuilder.Services.AddHttpClient<IBrowserCdpService, BrowserCdpService>();
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
