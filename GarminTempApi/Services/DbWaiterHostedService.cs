using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services
{
    public class DbWaiterHostedService : IHostedService
    {
        private readonly ILogger<DbWaiterHostedService> _logger;
        private readonly GarminDbReader _reader;
        private readonly int _timeoutSeconds = 120;

        public DbWaiterHostedService(ILogger<DbWaiterHostedService> logger, GarminDbReader reader)
        {
            _logger = logger;
            _reader = reader;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DbWaiterHostedService starting. Waiting for Garmin DB...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!await _reader.DbExistsAsync())
            {
                if (sw.Elapsed.TotalSeconds > _timeoutSeconds)
                {
                    _logger.LogWarning("Timed out waiting for Garmin DB after {Timeout}s.", _timeoutSeconds);
                    return;
                }
                _logger.LogInformation("Garmin DB not found yet. Sleeping 3s...");
                await Task.Delay(3000, cancellationToken);
            }

            _logger.LogInformation("Garmin DB found at startup.");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
