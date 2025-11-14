using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.AspNetCore.Mvc;
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
                .Select(a => new ActivityDto
                {
                    Id = a.Id,
                    ExternalId = a.ExternalId,
                    Source = a.Source,
                    FileName = a.FileName,
                    StartTime = a.StartTime,
                    DistanceMeters = a.DistanceMeters,
                    Duration = a.Duration,
                    ActivityType = a.ActivityType,
                    Points = a.Points
                        .OrderBy(p => p.Timestamp)
                        .Select(p => new ActivityPointDto
                        {
                            Id = p.Id,
                            ActivityId = p.ActivityId,
                            Timestamp = p.Timestamp,
                            Latitude = p.Latitude,
                            Longitude = p.Longitude,
                            HeartRate = p.HeartRate,
                            Altitude = p.Altitude
                        })
                        .ToList()
                })
                .ToListAsync();

            return Ok(activities);
        }

        private sealed class ActivityDto
        {
            public int Id { get; init; }
            public string ExternalId { get; init; } = string.Empty;
            public string Source { get; init; } = string.Empty;
            public string FileName { get; init; } = string.Empty;
            public DateTime StartTime { get; init; }
            public double DistanceMeters { get; init; }
            public TimeSpan Duration { get; init; }
            public string ActivityType { get; init; } = string.Empty;
            public List<ActivityPointDto> Points { get; init; } = new();
        }

        private sealed class ActivityPointDto
        {
            public int Id { get; init; }
            public int ActivityId { get; init; }
            public DateTime Timestamp { get; init; }
            public double Latitude { get; init; }
            public double Longitude { get; init; }
            public double? HeartRate { get; init; }
            public double? Altitude { get; init; }
        }
    }
}
