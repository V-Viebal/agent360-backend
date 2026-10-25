namespace Agent360.API.Features.Agents.Security;

/// <summary>
/// Background Reconciliation Worker (Upgrade #6):
/// Runs every 15 minutes (and once on boot) to reconcile all 31 Agent Identity Cells against
/// their Organization Cell baseline policies, updating the live telemetry status.
/// </summary>
public sealed class Iam360CellReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<Iam360CellReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var cellsService = scope.ServiceProvider.GetRequiredService<IIam360CellsService>();
                    var report = await cellsService.RunLiveTestAllAgentsAsync(stoppingToken);
                    logger.LogInformation(
                        "[IAM360 Reconciliation Worker] Verified {Passed}/{Total} Agent Identity Cells across {Orgs} Organization Cells",
                        report.PassedCount,
                        report.TotalAgentsTested,
                        report.OrganizationCellsTested);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "[IAM360 Reconciliation Worker] Periodic cell reconciliation cycle skipped");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown
        }
    }
}
