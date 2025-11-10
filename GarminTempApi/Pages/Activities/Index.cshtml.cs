using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Pages.Activities
{
    public class IndexModel : PageModel
    {
        private readonly AppDbContext _db;

        public IndexModel(AppDbContext db) => _db = db;

        public IReadOnlyList<ActivityRow> Activities { get; private set; } = Array.Empty<ActivityRow>();
        public IReadOnlyList<string> ActivityTypes { get; private set; } = Array.Empty<string>();

        [BindProperty(SupportsGet = true)]
        public DateTime? StartDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? EndDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? ActivityType { get; set; }

        public async Task OnGetAsync()
        {
            ActivityTypes = await _db.Activities
                .AsNoTracking()
                .Select(a => a.ActivityType)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .OrderBy(t => t)
                .ToListAsync();

            var query = _db.Activities
                .AsNoTracking()
                .AsQueryable();

            if (StartDate.HasValue)
            {
                var start = StartDate.Value.Date;
                query = query.Where(a => a.StartTime >= start);
            }

            if (EndDate.HasValue)
            {
                var exclusiveEnd = EndDate.Value.Date.AddDays(1);
                query = query.Where(a => a.StartTime < exclusiveEnd);
            }

            if (!string.IsNullOrWhiteSpace(ActivityType))
            {
                query = query.Where(a => a.ActivityType == ActivityType);
            }

            Activities = await query
                .OrderByDescending(a => a.StartTime)
                .Take(200)
                .Select(a => new ActivityRow
                {
                    ActivityId = a.ExternalId,
                    ActivityName = string.IsNullOrWhiteSpace(a.FileName) ? a.ExternalId : a.FileName,
                    StartTime = a.StartTime,
                    DistanceMeters = a.DistanceMeters,
                    Duration = a.Duration.ToString(@"hh\:mm\:ss"),
                    DurationSeconds = a.Duration.TotalSeconds,
                    Sport = a.ActivityType,
                    Source = a.Source,
                })
                .ToListAsync();
        }

        public class ActivityRow
        {
            public string ActivityId { get; set; } = string.Empty;
            public string ActivityName { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public double DistanceMeters { get; set; }
            public string Duration { get; set; } = string.Empty;
            public string Sport { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public double DurationSeconds { get; set; }
        }
    }
}
