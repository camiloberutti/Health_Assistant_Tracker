using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Pages.Activities
{
    public class IndexModel : PageModel
    {
        private readonly AppDbContext _db;

        public IndexModel(AppDbContext db) => _db = db;

        public IEnumerable<ActivityRow> Activities { get; set; } = Enumerable.Empty<ActivityRow>();

        public async Task OnGetAsync()
        {
            Activities = await _db.Activities
                .OrderByDescending(a => a.StartTime)
                .Take(100)
                .Select(a => new ActivityRow
                {
                    ActivityId = a.ExternalId,
                    ActivityName = a.FileName,
                    StartTime = a.StartTime,
                    DistanceMeters = a.DistanceMeters,
                    DurationSeconds = a.Duration.TotalSeconds,
                    Sport = a.ActivityType
                })
                .ToListAsync();
        }

        public class ActivityRow
        {
            public string ActivityId { get; set; } = string.Empty;
            public string ActivityName { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public double DistanceMeters { get; set; }
            public double DurationSeconds { get; set; }
            public string Sport { get; set; } = string.Empty;
        }
    }
}
