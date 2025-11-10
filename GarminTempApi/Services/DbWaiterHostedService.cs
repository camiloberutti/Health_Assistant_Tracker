using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

/// <summary>
/// Legacy placeholder retained to avoid breaking startup while the Garmin DB flow is removed.
/// </summary>
public class DbWaiterHostedService : IHostedService
{
    private readonly ILogger<DbWaiterHostedService> _logger;

    public DbWaiterHostedService(ILogger<DbWaiterHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DbWaiterHostedService is deprecated and performs no actions.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
