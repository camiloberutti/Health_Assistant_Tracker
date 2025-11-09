using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/activities")]
    public class ActivitiesController : ControllerBase
    {
        private readonly AppDbContext _db;
        public ActivitiesController(AppDbContext db) => _db = db;

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var activities = await _db.Activities
                .Include(a => a.Points)
                .ToListAsync();

            return Ok(activities);
        }
    }
}
