using Agent360.API.Features.Agents.Services;
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
            webAppBuilder.Services.AddHttpClient<IQwenPawClient, QwenPawClient>();
            webAppBuilder.Services.AddHttpClient<IBrowserCdpService, BrowserCdpService>();
            return webAppBuilder;
        }
    }
}
