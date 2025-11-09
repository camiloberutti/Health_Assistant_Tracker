using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

public class GarminSyncHostedService : BackgroundService
{
    private readonly GarminConnectImporter _importer;
    private readonly GarminDataSyncService _syncService;
    private readonly ILogger<GarminSyncHostedService> _logger;

    public GarminSyncHostedService(GarminConnectImporter importer, GarminDataSyncService syncService, ILogger<GarminSyncHostedService> logger)
    {
        _importer = importer;
        _syncService = syncService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_importer.HasCredentials())
        {
            _logger.LogWarning("Garmin credentials are not configured; skipping automatic synchronization.");
            return;
        }

        _logger.LogInformation("Garmin synchronization service starting.");
        var interval = _importer.GetRefreshInterval();

        do
        {
            try
            {
                var result = await _syncService.SynchronizeAsync(stoppingToken);
                _logger.LogInformation(
                    "Garmin sync complete. Inserted {ActivitiesInserted} of {ActivitiesFetched} activities, upserted {SleepUpserted} sleep and {StepsUpserted} step summaries.",
                    result.ActivitiesInserted,
                    result.ActivitiesFetched,
                    result.SleepUpserted,
                    result.StepsUpserted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Garmin synchronization failed: {Message}", ex.Message);
            }

            if (interval is null || interval.Value <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.Delay(interval.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
        while (!stoppingToken.IsCancellationRequested);

        _logger.LogInformation("Garmin synchronization service stopping.");
    }
}
