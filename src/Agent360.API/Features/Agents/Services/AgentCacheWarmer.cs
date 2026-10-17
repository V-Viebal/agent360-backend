using Agent360.API.Features.Agents.Endpoints;
using Microsoft.Extensions.Caching.Memory;

namespace Agent360.API.Features.Agents.Services;

/// <summary>
/// Keeps the fleet agent list and the slow per-agent capability lists (mcp, skills, tools) warm,
/// so GET /api/agents and the runtime proxy answer from cache instead of waiting on discovery.
/// </summary>
public sealed class AgentCacheWarmer(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    IConfiguration configuration,
    ILogger<AgentCacheWarmer> logger) : BackgroundService
{
    private static readonly string[] WarmRoutes = ["mcp", "skills", "tools"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Agent360:CacheWarmer:Enabled", true))
        {
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Max(10, configuration.GetValue("Agent360:CacheWarmer:IntervalSeconds", 40)));
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await WarmOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Agent cache warm cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task WarmOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IQwenPawClient>();
        var routing = scope.ServiceProvider.GetRequiredService<IFleetInstanceRoutingService>();

        var agents = await client.RefreshAgentsAsync(ct);
        var targets = agents
            .Where(a => a.Enabled)
            .Select(a => (a.Slug, Target: routing.ResolveTarget(a.Slug)))
            .Where(x => x.Target.IsQwenPaw)
            .SelectMany(x => WarmRoutes.Select(r => (x.Slug, x.Target, Route: r)))
            .ToList();

        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (t, token) =>
        {
            try
            {
                await AgentRuntimeProxyEndpoint.WarmAsync(routing, cache, t.Slug, t.Target, t.Route, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Warm {Route} for {Agent} failed", t.Route, t.Slug);
            }
        });
    }
}
