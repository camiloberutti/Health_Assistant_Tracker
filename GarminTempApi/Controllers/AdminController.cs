using GarminTempApi.Services;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/admin")]
    public class AdminController : ControllerBase
    {
        private readonly GarminDataSyncService _syncService;

        public AdminController(GarminDataSyncService syncService)
        {
            _syncService = syncService;
        }

        [HttpPost("trigger-import")]
        public async Task<IActionResult> TriggerImport(CancellationToken cancellationToken)
        {
            try
            {
                var syncResult = await _syncService.SynchronizeAsync(cancellationToken);
                return Ok(new
                {
                    executed = true,
                    activitiesInserted = syncResult.ActivitiesInserted,
                    activitiesFetched = syncResult.ActivitiesFetched,
                    sleepUpserted = syncResult.SleepUpserted,
                    stepsUpserted = syncResult.StepsUpserted,
                    stdout = syncResult.FetchResult.StandardOutput,
                    stderr = syncResult.FetchResult.StandardError
                });
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message);
            }
        }
    }
}
