using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Background
{
    public class GarminPollingService : BackgroundService
    {
        private readonly ILogger<GarminPollingService> _logger;

        public GarminPollingService(ILogger<GarminPollingService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GarminPollingService started (skeleton).");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // TODO: use GarminDbReader or API tokens to import new activities
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (TaskCanceledException) { /* shutting down */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in GarminPollingService loop.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
    }
}
