using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Pages.Steps;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db) => _db = db;

    public IReadOnlyList<StepRow> Items { get; private set; } = Array.Empty<StepRow>();

    public async Task OnGetAsync()
    {
        Items = await _db.StepSummaries
            .OrderByDescending(s => s.Date)
            .Take(30)
            .Select(s => new StepRow
            {
                Date = s.Date,
                TotalSteps = s.TotalSteps,
                GoalSteps = s.GoalSteps,
                TotalCalories = s.TotalCalories,
                ActiveCalories = s.ActiveCalories,
                TotalDistanceMeters = s.TotalDistanceMeters
            })
            .ToListAsync();
    }

    public class StepRow
    {
        public DateTime Date { get; set; }
        public double TotalSteps { get; set; }
        public double GoalSteps { get; set; }
        public double TotalCalories { get; set; }
        public double ActiveCalories { get; set; }
        public double TotalDistanceMeters { get; set; }
    }
}
