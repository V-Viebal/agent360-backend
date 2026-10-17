using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.SubAgents;

/// <summary>
/// Background lifecycle for the delegation ledger:
/// polls running async tasks + enforces timeouts, auto-ingests native A2A tool calls for bound
/// parents (Phase 4), and archives old terminal tasks. Every pass is isolated and best-effort.
/// </summary>
public sealed class DelegationMonitor(
    IServiceScopeFactory scopes,
    SubAgentStore store,
    IOptionsMonitor<SubAgentOptions> options,
    ILogger<DelegationMonitor> logger) : BackgroundService
{
    private DateTimeOffset _lastIngest = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCompact = DateTimeOffset.MinValue;

    public DateTimeOffset? LastRunAt { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.Enabled && opt.MonitorEnabled)
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, opt.MonitorIntervalSeconds)), stoppingToken);
        }
    }

    internal async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        using var scope = scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<SubAgentService>();
        try
        {
            // Keep the native agent inventory warm so bind/health never wait on a cold QwenPaw /api/agents.
            await scope.ServiceProvider.GetRequiredService<IQwenPawA2AClient>().ListAgentsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Agent inventory warm failed");
        }
        try
        {
            var changed = await svc.MonitorOnceAsync(ct);
            if (changed > 0) logger.LogInformation("Delegation monitor updated {Count} task(s)", changed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Delegation monitor pass failed");
        }

        if (opt.IngestIntervalSeconds > 0 && now - _lastIngest >= TimeSpan.FromSeconds(opt.IngestIntervalSeconds))
        {
            _lastIngest = now;
            foreach (var parent in await svc.BoundParentsAsync())
            {
                try
                {
                    var n = await svc.IngestNativeAsync(parent, Math.Clamp(opt.IngestMaxChats, 1, 50), ct);
                    if (n.Count > 0) logger.LogInformation("Auto-ledger ingested {Count} native delegation(s) for {Parent}", n.Count, parent);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Auto-ledger ingest failed for {Parent}", parent);
                }
            }
        }

        if (now - _lastCompact >= TimeSpan.FromHours(1))
        {
            _lastCompact = now;
            try
            {
                var archived = await store.CompactAsync(TimeSpan.FromDays(Math.Max(1, opt.RetentionDays)), opt.MaxLedgerTasks, now);
                if (archived > 0) logger.LogInformation("Archived {Count} terminal delegation(s)", archived);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Ledger compaction failed");
            }
        }
        LastRunAt = now;
    }
}
