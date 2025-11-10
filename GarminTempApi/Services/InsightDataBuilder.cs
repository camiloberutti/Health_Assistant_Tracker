using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

public class InsightDataBuilder
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<InsightDataBuilder> _logger;

    public InsightDataBuilder(AppDbContext dbContext, ILogger<InsightDataBuilder> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<UserDataDigest> BuildUserDataDigestAsync(DateTime rangeStartInclusive, DateTime rangeEndInclusive, CancellationToken cancellationToken)
    {
        if (rangeEndInclusive < rangeStartInclusive)
        {
            throw new ArgumentException("Range end must not be before range start.", nameof(rangeEndInclusive));
        }

        var startDate = rangeStartInclusive.Date;
        var endDate = rangeEndInclusive.Date;
        var endExclusive = endDate.AddDays(1);

        var stats = new SortedDictionary<DateTime, DailyAccumulator>();

        DailyAccumulator GetOrCreateAccumulator(DateTime date)
        {
            var key = date.Date;
            if (!stats.TryGetValue(key, out var acc))
            {
                acc = new DailyAccumulator();
                stats[key] = acc;
            }

            return acc;
        }

        var stepSummaries = await _dbContext.StepSummaries
            .Where(s => s.Date >= startDate && s.Date < endExclusive)
            .ToListAsync(cancellationToken);

        foreach (var summary in stepSummaries)
        {
            var acc = GetOrCreateAccumulator(summary.Date);
            acc.Steps = summary.TotalSteps;
        }

        var sleepSummaries = await _dbContext.SleepSummaries
            .Where(s => s.Date >= startDate && s.Date < endExclusive)
            .ToListAsync(cancellationToken);

        foreach (var sleep in sleepSummaries)
        {
            var acc = GetOrCreateAccumulator(sleep.Date);
            acc.SleepSeconds = sleep.TotalSleepSeconds;
            acc.BodyBatteryChange = sleep.BodyBatteryChange;
            acc.RestingHeartRate = sleep.RestingHeartRate;
        }

        var activities = await _dbContext.Activities
            .Where(a => a.StartTime >= startDate && a.StartTime < endExclusive)
            .Select(a => new ActivityProjection
            {
                Date = a.StartTime,
                DistanceMeters = a.DistanceMeters,
                DurationTicks = a.Duration.Ticks,
                ActivityType = a.ActivityType
            })
            .ToListAsync(cancellationToken);

        foreach (var activity in activities)
        {
            var acc = GetOrCreateAccumulator(activity.Date);
            var durationMinutes = TimeSpan.FromTicks(activity.DurationTicks).TotalMinutes;
            acc.ActiveMinutes = (acc.ActiveMinutes ?? 0d) + durationMinutes;
            acc.DistanceKm = (acc.DistanceKm ?? 0d) + activity.DistanceMeters / 1000d;
            if (!string.IsNullOrWhiteSpace(activity.ActivityType))
            {
                acc.ActivityTypes.Add(activity.ActivityType);
            }
        }

        var dailyStats = stats.Select(pair => new UserDailyStat(
                pair.Key,
                pair.Value.Steps,
                pair.Value.SleepSeconds,
                pair.Value.ActiveMinutes,
                pair.Value.DistanceKm,
                pair.Value.BodyBatteryChange,
                pair.Value.RestingHeartRate,
                pair.Value.ActivityTypes.ToArray()))
            .OrderBy(stat => stat.Date)
            .ToList();

        var totalDistance = dailyStats.Sum(s => s.DistanceKm ?? 0d);
        var totalActiveMinutes = dailyStats.Sum(s => s.ActiveMinutes ?? 0d);
        var averageSleep = Average(dailyStats.Select(s => s.SleepSeconds));
        var averageSteps = Average(dailyStats.Select(s => s.Steps));
        var averageRestingHeartRate = Average(dailyStats.Select(s => s.RestingHeartRate));

        return new UserDataDigest(
            RangeStart: startDate,
            RangeEnd: endDate,
            DailyStats: dailyStats,
            TotalDistanceKm: totalDistance,
            TotalActiveMinutes: totalActiveMinutes,
            AverageSleepSeconds: averageSleep,
            AverageSteps: averageSteps,
            AverageRestingHeartRate: averageRestingHeartRate);
    }

    public DailyRecommendationContext BuildDailyRecommendationContext(DateTime targetDate, UserDataDigest digest, int lookbackDays = 7)
    {
        if (lookbackDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lookbackDays), "Lookback days must be positive.");
        }

        var day = targetDate.Date;
        var startWindow = day.AddDays(-lookbackDays + 1);
        var relevantStats = digest.DailyStats
            .Where(s => s.Date >= startWindow && s.Date <= day)
            .OrderBy(s => s.Date)
            .ToList();

        UserDailyStat? todaysStats = relevantStats.LastOrDefault(s => s.Date == day);
        todaysStats ??= digest.DailyStats.LastOrDefault(s => s.Date == day);

        var dominantActivities = relevantStats
            .SelectMany(s => s.ActivityTypes ?? Array.Empty<string>())
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .GroupBy(type => type)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(g => g.Key)
            .ToList();

        return new DailyRecommendationContext(
            TargetDate: day,
            Steps: todaysStats?.Steps,
            StepsAverage: Average(relevantStats.Select(s => s.Steps)),
            ActiveMinutes: todaysStats?.ActiveMinutes,
            ActiveMinutesAverage: Average(relevantStats.Select(s => s.ActiveMinutes)),
            DistanceKm: todaysStats?.DistanceKm,
            DistanceKmAverage: Average(relevantStats.Select(s => s.DistanceKm)),
            SleepSeconds: todaysStats?.SleepSeconds,
            SleepSecondsAverage: Average(relevantStats.Select(s => s.SleepSeconds)),
            BodyBatteryChange: todaysStats?.BodyBatteryChange,
            RestingHeartRate: todaysStats?.RestingHeartRate,
            DominantActivityTypes: dominantActivities);
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var filtered = values
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        return filtered.Count == 0 ? null : filtered.Average();
    }

    private sealed class DailyAccumulator
    {
        public double? Steps { get; set; }
        public double? SleepSeconds { get; set; }
        public double? ActiveMinutes { get; set; }
        public double? DistanceKm { get; set; }
        public double? BodyBatteryChange { get; set; }
        public double? RestingHeartRate { get; set; }
        public List<string> ActivityTypes { get; } = new();
    }

    private sealed record ActivityProjection
    {
        public DateTime Date { get; init; }
        public double DistanceMeters { get; init; }
        public long DurationTicks { get; init; }
        public string ActivityType { get; init; } = string.Empty;
    }
}
