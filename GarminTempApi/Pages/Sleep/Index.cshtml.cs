using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Pages.Sleep;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db) => _db = db;

    public IReadOnlyList<SleepRow> Items { get; private set; } = Array.Empty<SleepRow>();

    public async Task OnGetAsync()
    {
        Items = await _db.SleepSummaries
            .OrderByDescending(s => s.Date)
            .Take(30)
            .Select(s => new SleepRow
            {
                Date = s.Date,
                TotalSleepSeconds = s.TotalSleepSeconds,
                DeepSleepSeconds = s.DeepSleepSeconds,
                LightSleepSeconds = s.LightSleepSeconds,
                RemSleepSeconds = s.RemSleepSeconds,
                AwakeSeconds = s.AwakeSeconds,
                SleepScore = s.SleepScore,
                SleepQualityType = s.SleepQualityType
            })
            .ToListAsync();
    }

    public class SleepRow
    {
        public DateTime Date { get; set; }
        public double TotalSleepSeconds { get; set; }
        public double DeepSleepSeconds { get; set; }
        public double LightSleepSeconds { get; set; }
        public double RemSleepSeconds { get; set; }
        public double AwakeSeconds { get; set; }
        public double? SleepScore { get; set; }
        public string SleepQualityType { get; set; } = string.Empty;
    }
}
