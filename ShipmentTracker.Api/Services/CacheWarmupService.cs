using Microsoft.Extensions.Options;
using ShipmentTracker.Api.Options;

namespace ShipmentTracker.Api.Services;

/// <summary>
/// Reads the recent reports (last <c>WarmDays</c> days) of every application in the background, now and then every
/// <c>WarmIntervalMinutes</c> minutes, so when someone presses "Load data" the files are already read and the numbers
/// appear at once. Only new or changed files are read from the NAS.
/// </summary>
public sealed class CacheWarmupService(
    ShipmentRepositories repositories,
    IOptionsMonitor<NasOptions> options,
    ILogger<CacheWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.WarmDays > 0)
            {
                foreach (var app in opt.EffectiveApplications())
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    var repo = repositories.Get(app.Id);
                    if (repo is null) continue;
                    try { await repo.WarmAsync(opt.WarmDays, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogWarning("Warm-up of {App} skipped: {Message}", app.Id, ex.Message); }
                }
            }

            var wait = TimeSpan.FromMinutes(Math.Max(1, opt.WarmIntervalMinutes));
            try { await Task.Delay(wait, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
