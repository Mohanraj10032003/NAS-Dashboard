using Microsoft.Extensions.Options;
using ShipmentTracker.Api.Options;
using ShipmentTracker.Api.Services.Nas;

namespace ShipmentTracker.Api.Services;

/// <summary>
/// Opens the ROOT folder once at start-up (logging in if needed), so a wrong path or password shows up at once in the log and in /api/health.
/// It reads no Excel file. Data is read only when a user presses "Load data".
/// </summary>
public sealed class NasStartupCheck(
    ShipmentRepositories repositories,
    INasConnector nas,
    IOptionsMonitor<NasOptions> options,
    ILogger<NasStartupCheck> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await nas.EnsureConnectedAsync(options.CurrentValue, stoppingToken);
            repositories.RootError = null;
            logger.LogInformation("NAS root is reachable: {Path}", options.CurrentValue.Path);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            repositories.RootError = ex.Message;
            logger.LogWarning("NAS root is NOT reachable: {Message}", ex.Message);
        }
        finally
        {
            repositories.RootChecked = true;
        }
    }
}
