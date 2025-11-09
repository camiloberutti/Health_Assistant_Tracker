using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/garmindb")]
    public class GarminDbController : ControllerBase
    {
        private readonly GarminTempApi.Services.GarminDbReader _reader;
        public GarminDbController(GarminTempApi.Services.GarminDbReader reader) => _reader = reader;

        [HttpGet("activities")]
        public async Task<IActionResult> GetActivities([FromQuery] int limit = 50)
        {
            if (!await _reader.DbExistsAsync())
                return Problem("Garmin DB not available yet.", statusCode: 503);

            var rows = await _reader.GetActivitiesAsync(limit);
            return Ok(rows);
        }
    }
}
