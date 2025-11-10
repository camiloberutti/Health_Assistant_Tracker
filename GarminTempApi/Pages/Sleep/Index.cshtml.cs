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
    public IReadOnlyList<SleepRow> RecentItems { get; private set; } = Array.Empty<SleepRow>();
    public IReadOnlyDictionary<string, SleepRangePayload> RangePayloads { get; private set; } =
        new Dictionary<string, SleepRangePayload>(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        var rows = await _db.SleepSummaries
            .OrderBy(s => s.Date)
            .Select(s => new SleepRow
            {
                Date = s.Date,
                TotalSleepSeconds = s.TotalSleepSeconds,
                DeepSleepSeconds = s.DeepSleepSeconds,
                LightSleepSeconds = s.LightSleepSeconds,
                RemSleepSeconds = s.RemSleepSeconds,
                AwakeSeconds = s.AwakeSeconds,
                SleepScore = s.SleepScore,
                SleepQualityType = s.SleepQualityType,
                SleepStartLocal = s.SleepStartLocal,
                SleepEndLocal = s.SleepEndLocal,
                SleepStartGmt = s.SleepStartGmt,
                SleepEndGmt = s.SleepEndGmt,
                RestingHeartRate = s.RestingHeartRate,
                BodyBatteryChange = s.BodyBatteryChange,
                AverageRespirationValue = s.AverageRespirationValue,
                LowestSpO2Value = s.LowestSpO2Value,
                SleepGoalSeconds = s.SleepGoalSeconds
            })
            .ToListAsync();

        Items = rows;
        RecentItems = rows
            .OrderByDescending(r => r.Date)
            .Take(30)
            .ToList();

        RangePayloads = BuildRangePayloads(rows);
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
        public DateTime? SleepStartLocal { get; set; }
        public DateTime? SleepEndLocal { get; set; }
        public DateTime? SleepStartGmt { get; set; }
        public DateTime? SleepEndGmt { get; set; }
        public double? RestingHeartRate { get; set; }
        public double? BodyBatteryChange { get; set; }
        public double? AverageRespirationValue { get; set; }
        public double? LowestSpO2Value { get; set; }
        public double? SleepGoalSeconds { get; set; }
    }

    public record SleepChartPoint(
        string Label,
        double TotalSeconds,
        double DeepSeconds,
        double LightSeconds,
        double RemSeconds,
        double AwakeSeconds,
        int SampleCount);

    public record SleepRangePayload(
        string Key,
        string DisplayLabel,
        string? Subtitle,
        IReadOnlyList<SleepChartPoint> Points,
        double? AverageTotalSeconds,
        double? AverageGoalDeltaSeconds,
        double? AverageScore,
        double? AverageRestingHeartRate,
        double? AverageRespiration,
        double? MinimumSpO2,
        double? AverageBodyBatteryChange,
        IReadOnlyDictionary<string, int> QualityCounts);

    private static IReadOnlyDictionary<string, SleepRangePayload> BuildRangePayloads(IReadOnlyList<SleepRow> rows)
    {
        var result = new Dictionary<string, SleepRangePayload>(StringComparer.OrdinalIgnoreCase);

        if (rows.Count == 0)
        {
            return result;
        }

        var lastDate = rows[^1].Date.Date;

        var daily = BuildDailyPayload(
            "7d",
            "Last 7 nights",
            "Daily totals",
            rows,
            lastDate.AddDays(-6),
            lastDate);
        if (daily.Points.Count > 0)
        {
            result[daily.Key] = daily;
        }

        var weekly = BuildWeeklyPayload(
            "4w",
            "Last 4 weeks",
            "Weekly averages per night",
            rows,
            lastDate.AddDays(-27),
            lastDate);
        if (weekly.Points.Count > 0)
        {
            result[weekly.Key] = weekly;
        }

        var monthly = BuildMonthlyPayload(
            "1y",
            "Last 12 months",
            "Monthly averages per night",
            rows,
            lastDate);
        if (monthly.Points.Count > 0)
        {
            result[monthly.Key] = monthly;
        }

        return result;
    }

    private static SleepRangePayload BuildDailyPayload(
        string key,
        string displayLabel,
        string subtitle,
        IReadOnlyList<SleepRow> rows,
        DateTime fromInclusive,
        DateTime toInclusive)
    {
        var subset = rows
            .Where(row => row.Date.Date >= fromInclusive.Date && row.Date.Date <= toInclusive.Date)
            .OrderBy(row => row.Date)
            .ToList();

        if (subset.Count == 0)
        {
            return EmptyRange(key, displayLabel, subtitle);
        }

        var points = subset
            .Select(row => new SleepChartPoint(
                row.Date.ToString("MMM dd"),
                row.TotalSleepSeconds,
                row.DeepSleepSeconds,
                row.LightSleepSeconds,
                row.RemSleepSeconds,
                row.AwakeSeconds,
                1))
            .ToList();

        return CreatePayload(key, displayLabel, subtitle, points, subset);
    }

    private static SleepRangePayload BuildWeeklyPayload(
        string key,
        string displayLabel,
        string subtitle,
        IReadOnlyList<SleepRow> rows,
        DateTime fromInclusive,
        DateTime toInclusive)
    {
        var from = fromInclusive.Date;
        var to = toInclusive.Date;

        var subset = rows
            .Where(row => row.Date.Date >= from && row.Date.Date <= to)
            .OrderBy(row => row.Date)
            .ToList();

        if (subset.Count == 0)
        {
            return EmptyRange(key, displayLabel, subtitle);
        }

        var grouped = subset
            .GroupBy(row => GetWeekStart(row.Date))
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Start = group.Key,
                End = group.Key.AddDays(6),
                Items = group.ToList()
            })
            .ToList();

        if (grouped.Count == 0)
        {
            return EmptyRange(key, displayLabel, subtitle);
        }

        var relevantGroups = grouped.Count > 4
            ? grouped.Skip(grouped.Count - 4).ToList()
            : grouped;

        var relevantItems = relevantGroups
            .SelectMany(group => group.Items)
            .OrderBy(row => row.Date)
            .ToList();

        if (relevantItems.Count == 0)
        {
            return EmptyRange(key, displayLabel, subtitle);
        }

        var points = relevantGroups
            .Select(group => new SleepChartPoint(
                $"{group.Start:MMM dd} - {group.End:MMM dd}",
                group.Items.Average(item => item.TotalSleepSeconds),
                group.Items.Average(item => item.DeepSleepSeconds),
                group.Items.Average(item => item.LightSleepSeconds),
                group.Items.Average(item => item.RemSleepSeconds),
                group.Items.Average(item => item.AwakeSeconds),
                group.Items.Count))
            .ToList();

        return CreatePayload(key, displayLabel, subtitle, points, relevantItems);
    }

    private static SleepRangePayload BuildMonthlyPayload(
        string key,
        string displayLabel,
        string subtitle,
        IReadOnlyList<SleepRow> rows,
        DateTime lastDate)
    {
        var lastMonthStart = GetMonthStart(lastDate);
        var fromMonthStart = lastMonthStart.AddMonths(-11);

        var subset = rows
            .Where(row => row.Date.Date >= fromMonthStart.Date && row.Date.Date <= lastDate.Date)
            .OrderBy(row => row.Date)
            .ToList();

        if (subset.Count == 0)
        {
            return EmptyRange(key, displayLabel, subtitle);
        }

        var grouped = subset
            .GroupBy(row => GetMonthStart(row.Date))
            .OrderBy(group => group.Key)
            .ToList();

        if (grouped.Count == 0)
        {
            return EmptyRange(key, displayLabel, subtitle);
        }

        var relevantGroups = grouped.Count > 12
            ? grouped.Skip(grouped.Count - 12).ToList()
            : grouped;

        var relevantItems = relevantGroups
            .SelectMany(group => group)
            .OrderBy(row => row.Date)
            .ToList();

        if (relevantItems.Count == 0)
        {
            return EmptyRange(key, displayLabel, subtitle);
        }

        var points = relevantGroups
            .Select(group =>
            {
                var items = group.ToList();
                return new SleepChartPoint(
                    group.Key.ToString("MMM yyyy"),
                    items.Average(item => item.TotalSleepSeconds),
                    items.Average(item => item.DeepSleepSeconds),
                    items.Average(item => item.LightSleepSeconds),
                    items.Average(item => item.RemSleepSeconds),
                    items.Average(item => item.AwakeSeconds),
                    items.Count);
            })
            .ToList();

        return CreatePayload(key, displayLabel, subtitle, points, relevantItems);
    }

    private static SleepRangePayload CreatePayload(
        string key,
        string displayLabel,
        string subtitle,
        IReadOnlyList<SleepChartPoint> points,
        IReadOnlyList<SleepRow> subset)
    {
        return new SleepRangePayload(
            key,
            displayLabel,
            subtitle,
            points,
            Average(subset, row => row.TotalSleepSeconds),
            AverageGoalDelta(subset),
            AverageNullable(subset, row => row.SleepScore),
            AverageNullable(subset, row => row.RestingHeartRate),
            AverageNullable(subset, row => row.AverageRespirationValue),
            MinimumNullable(subset, row => row.LowestSpO2Value),
            AverageNullable(subset, row => row.BodyBatteryChange),
            BuildQualityCounts(subset));
    }

    private static SleepRangePayload EmptyRange(string key, string displayLabel, string subtitle)
    {
        return new SleepRangePayload(
            key,
            displayLabel,
            subtitle,
            Array.Empty<SleepChartPoint>(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, int>());
    }

    private static DateTime GetWeekStart(DateTime value)
    {
        var date = value.Date;
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    private static DateTime GetMonthStart(DateTime value) => new DateTime(value.Year, value.Month, 1);

    private static double? Average(IReadOnlyList<SleepRow> rows, Func<SleepRow, double> selector)
    {
        return rows.Count == 0 ? null : rows.Average(selector);
    }

    private static double? AverageGoalDelta(IReadOnlyList<SleepRow> rows)
    {
        var deltas = rows
            .Where(row => row.SleepGoalSeconds.HasValue)
            .Select(row => row.TotalSleepSeconds - row.SleepGoalSeconds!.Value)
            .ToList();

        return deltas.Count == 0 ? null : deltas.Average();
    }

    private static double? AverageNullable(IReadOnlyList<SleepRow> rows, Func<SleepRow, double?> selector)
    {
        var values = rows
            .Select(selector)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        return values.Count == 0 ? null : values.Average();
    }

    private static double? MinimumNullable(IReadOnlyList<SleepRow> rows, Func<SleepRow, double?> selector)
    {
        var values = rows
            .Select(selector)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        return values.Count == 0 ? null : values.Min();
    }

    private static IReadOnlyDictionary<string, int> BuildQualityCounts(IEnumerable<SleepRow> rows)
    {
        var counts = rows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.SleepQualityType) ? "Unknown" : row.SleepQualityType.Trim())
            .OrderByDescending(group => group.Count());

        var result = new Dictionary<string, int>();
        foreach (var group in counts)
        {
            result[group.Key] = group.Count();
        }

        return result;
    }
}
