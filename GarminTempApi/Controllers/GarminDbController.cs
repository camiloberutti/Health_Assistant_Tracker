using System.Linq;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/garmindb")]
    public class GarminDbController : ControllerBase
    {
        private readonly AppDbContext _db;
        public GarminDbController(AppDbContext db) => _db = db;

        [HttpGet("activities")]
        public async Task<IActionResult> GetActivities([FromQuery] int limit = 50)
        {
            var rows = await _db.Activities
                .OrderByDescending(a => a.StartTime)
                .Take(Math.Max(1, Math.Min(limit, 500)))
                .Select(a => new
                {
                    a.Id,
                    a.ExternalId,
                    a.ActivityType,
                    a.DistanceMeters,
                    DurationSeconds = a.Duration.TotalSeconds,
                    a.StartTime,
                    a.Source,
                    a.FileName
                })
                .ToListAsync();

            return Ok(rows);
        }
    }
}
