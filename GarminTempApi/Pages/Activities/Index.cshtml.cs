using System;
using System.Collections.Generic;
using System.Globalization;
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
        public IReadOnlyList<ActivityTypeOption> ActivityTypes { get; private set; } = Array.Empty<ActivityTypeOption>();
        public string LatestActivityDateDisplay { get; private set; } = "--";

        [BindProperty(SupportsGet = true)]
        public DateTime? StartDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? EndDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? ActivityType { get; set; }

        public async Task OnGetAsync()
        {
            if (Request.Query.TryGetValue(nameof(StartDate), out var startValues) && TryParseShortDate(startValues.FirstOrDefault(), out var parsedStart))
            {
                StartDate = parsedStart;
            }

            if (Request.Query.TryGetValue(nameof(EndDate), out var endValues) && TryParseShortDate(endValues.FirstOrDefault(), out var parsedEnd))
            {
                EndDate = parsedEnd;
            }

            var rawTypes = await _db.Activities
                .AsNoTracking()
                .Select(a => a.ActivityType)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToListAsync();

            ActivityTypes = rawTypes
                .Select(t => new ActivityTypeOption(t!, ToDisplayLabel(t)))
                .OrderBy(t => t.Label)
                .ToList();

            var latestActivityStart = await _db.Activities
                .AsNoTracking()
                .OrderByDescending(a => a.StartTime)
                .Select(a => (DateTime?)a.StartTime)
                .FirstOrDefaultAsync();

            LatestActivityDateDisplay = latestActivityStart.HasValue
                ? latestActivityStart.Value.ToLocalTime().ToString("dd MMM yy", CultureInfo.InvariantCulture)
                : "--";

            var oldestActivityStart = await _db.Activities
                .AsNoTracking()
                .OrderBy(a => a.StartTime)
                .Select(a => (DateTime?)a.StartTime)
                .FirstOrDefaultAsync();

            StartDate = (StartDate?.Date) ?? oldestActivityStart?.Date;
            EndDate = (EndDate?.Date) ?? DateTime.Today;

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
                    Sport = a.ActivityType ?? string.Empty,
                    SportDisplay = ToDisplayLabel(a.ActivityType),
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
            public string SportDisplay { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public double DurationSeconds { get; set; }
        }

        public record ActivityTypeOption(string Value, string Label);

        private static string ToDisplayLabel(string? rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType))
            {
                return "Activity";
            }

            var cleaned = rawType
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Trim();

            if (cleaned.Length == 0)
            {
                return "Activity";
            }

            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned);
        }

        private static bool TryParseShortDate(string? input, out DateTime result)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                result = default;
                return false;
            }

            if (DateTime.TryParseExact(input.Trim(), new[] { "dd MMM yy", "d MMM yy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                result = parsed.Date;
                return true;
            }

            result = default;
            return false;
        }
    }
}
