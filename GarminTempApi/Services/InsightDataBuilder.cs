using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using GarminTempApi.Configuration;
using GarminTempApi.Utilities;
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

    public async Task<CalendarInsightPayload> BuildCalendarInsightAsync(DateTime targetDate, CalendarRecommendationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var today = DateTime.SpecifyKind(targetDate.Date, DateTimeKind.Local);
        var lookbackDays = Math.Max(3, options.RaceRecoveryWindowDays);
        var forwardDays = Math.Max(options.RaceLookaheadDays, Math.Max(options.RacePreparationWindowDays, 4));

        var rangeStartLocal = today.AddDays(-lookbackDays);
        var rangeEndExclusiveLocal = today.AddDays(forwardDays + 1);

        var rangeStartDate = DateOnly.FromDateTime(rangeStartLocal);
        var rangeEndExclusiveDate = DateOnly.FromDateTime(rangeEndExclusiveLocal);

        var entities = await _dbContext.RoutineEvents
            .Where(e =>
                (!e.IsRecurring && e.StartLocal < rangeEndExclusiveLocal && e.EndLocal >= rangeStartLocal) ||
                (e.IsRecurring &&
                 (e.RecurrenceEndLocal ?? DateTime.MaxValue) >= rangeStartLocal &&
                 (e.RecurrenceStartLocal ?? e.StartLocal) < rangeEndExclusiveLocal))
            .ToListAsync(cancellationToken);

        var occurrences = new List<RoutineEventToolkit.RoutineEventOccurrence>(capacity: entities.Count);

        foreach (var entity in entities)
        {
            if (entity.IsRecurring && !string.IsNullOrWhiteSpace(entity.RecurrenceDays))
            {
                occurrences.AddRange(RoutineEventToolkit.ExpandOccurrences(entity, rangeStartDate, rangeEndExclusiveDate));
            }
            else if (entity.EndLocal >= rangeStartLocal && entity.StartLocal < rangeEndExclusiveLocal)
            {
                occurrences.Add(new RoutineEventToolkit.RoutineEventOccurrence(
                    entity,
                    entity.StartLocal.AsLocalTime(),
                    entity.EndLocal.AsLocalTime(),
                    Array.Empty<int>()));
            }
        }

        occurrences.Sort((a, b) => a.StartLocal.CompareTo(b.StartLocal));

        List<RoutineEventSnapshot> SelectWindow(int startOffsetDays, int dayCount)
        {
            var windowStart = today.AddDays(startOffsetDays);
            var windowEnd = windowStart.AddDays(dayCount);

            return occurrences
                .Where(o => o.StartLocal >= windowStart && o.StartLocal < windowEnd)
                .Select(CreateSnapshot)
                .ToList();
        }

        var pastThreeDays = SelectWindow(-3, 3);
        var todayEvents = SelectWindow(0, 1);
        var tomorrowEvents = SelectWindow(1, 1);
        var nextThreeDays = SelectWindow(2, 3);

        var raceFocus = occurrences
            .Where(o => o.Event.IsRace)
            .Select(o => CreateRaceInsight(o, today, options))
            .Where(r => r is not null)
            .Cast<RaceEventInsight>()
            .DistinctBy(r => (r.Id, DateOnly.FromDateTime(r.StartLocal)))
            .OrderBy(r => r.StartLocal)
            .ToList();

        var configuration = new CalendarRaceConfiguration(
            PreparationWindowDays: options.RacePreparationWindowDays,
            TaperWindowDays: options.RaceTaperWindowDays,
            RecoveryWindowDays: options.RaceRecoveryWindowDays,
            LookaheadDays: options.RaceLookaheadDays);

        return new CalendarInsightPayload(
            Today: today,
            PastThreeDays: pastThreeDays,
            TodayEvents: todayEvents,
            TomorrowEvents: tomorrowEvents,
            NextThreeDays: nextThreeDays,
            RaceFocus: raceFocus,
            RaceConfiguration: configuration);
    }

    public async Task<WeeklyHealthSnapshot> BuildWeeklyHealthSnapshotAsync(DateTime targetDate, int lookbackDays = 7, CancellationToken cancellationToken = default)
    {
        if (lookbackDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lookbackDays), "Lookback days must be positive.");
        }

        var endDate = DateOnly.FromDateTime(targetDate.Date);
        var startDate = endDate.AddDays(-lookbackDays + 1);

        var dayRange = Enumerable.Range(0, lookbackDays)
            .Select(offset => startDate.AddDays(offset))
            .ToArray();

        var stepSummaries = await _dbContext.StepSummaries
            .Where(s => s.Date >= startDate.ToDateTime(TimeOnly.MinValue) && s.Date <= endDate.ToDateTime(TimeOnly.MinValue))
            .Select(s => new { s.Date, s.TotalSteps, s.TotalDistanceMeters, s.TotalCalories })
            .ToListAsync(cancellationToken);

        var sleepSummaries = await _dbContext.SleepSummaries
            .Where(s => s.Date >= startDate.ToDateTime(TimeOnly.MinValue) && s.Date <= endDate.ToDateTime(TimeOnly.MinValue))
            .ToListAsync(cancellationToken);

        var activities = await _dbContext.Activities
            .Where(a => a.StartTime >= startDate.ToDateTime(TimeOnly.MinValue) && a.StartTime <= endDate.ToDateTime(TimeOnly.MaxValue))
            .Select(a => new ActivityProjection
            {
                Date = a.StartTime,
                DistanceMeters = a.DistanceMeters,
                DurationTicks = a.Duration.Ticks,
                ActivityType = a.ActivityType
            })
            .ToListAsync(cancellationToken);

        var dailySteps = dayRange.ToDictionary(d => d, _ => 0d);
        var dailyActiveMinutes = dayRange.ToDictionary(d => d, _ => 0d);

        foreach (var step in stepSummaries)
        {
            var date = DateOnly.FromDateTime(step.Date);
            if (dailySteps.ContainsKey(date))
            {
                dailySteps[date] = step.TotalSteps;
            }
        }

        foreach (var activity in activities)
        {
            var date = DateOnly.FromDateTime(activity.Date);
            if (dailyActiveMinutes.ContainsKey(date))
            {
                dailyActiveMinutes[date] += TimeSpan.FromTicks(activity.DurationTicks).TotalMinutes;
            }
        }

        var stepDays = dayRange
            .Select(day => new WeeklyStepDay(day, dailySteps[day]))
            .ToList();

        var totalDistanceKm = stepSummaries.Sum(s => s.TotalDistanceMeters) / 1000d;
        double? averageSteps = stepDays.Count == 0 ? null : stepDays.Average(d => d.Steps);

        var totalCalories = stepSummaries.Sum(s => s.TotalCalories);

        double? AverageSeconds(Func<SleepSummary, double> selector)
        {
            var values = sleepSummaries.Select(selector).Where(v => v > 0).ToList();
            return values.Count == 0 ? null : values.Average();
        }

        var averageSleepSeconds = AverageSeconds(s => s.TotalSleepSeconds);
        var deepSleepSeconds = AverageSeconds(s => s.DeepSleepSeconds);
        var remSleepSeconds = AverageSeconds(s => s.RemSleepSeconds);

        double? averageSleepScore = sleepSummaries
            .Select(s => s.SleepScore)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .DefaultIfEmpty()
            .Average();

        if (sleepSummaries.All(s => !s.SleepScore.HasValue))
        {
            averageSleepScore = null;
        }

        var restSummary = ClassifyRestDays(stepDays, dailyActiveMinutes);

        var activitySummaries = activities
            .Select(BuildWeeklyActivitySummary)
            .ToList();

        return new WeeklyHealthSnapshot(
            RangeStart: startDate,
            RangeEnd: endDate,
            Steps: new WeeklyStepSummary(
                Daily: stepDays,
                TotalDistanceKm: totalDistanceKm,
                AverageSteps: averageSteps),
            Sleep: new WeeklySleepSummary(
                AverageHours: ConvertSecondsToHours(averageSleepSeconds),
                DeepSleepHours: ConvertSecondsToHours(deepSleepSeconds),
                RemSleepHours: ConvertSecondsToHours(remSleepSeconds),
                AverageSleepScore: averageSleepScore),
            RestDays: restSummary,
            Activities: activitySummaries,
            TotalCaloriesBurned: totalCalories > 0 ? totalCalories : null);
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

    private static WeeklyRestSummary ClassifyRestDays(IReadOnlyList<WeeklyStepDay> steps, IReadOnlyDictionary<DateOnly, double> activeMinutes)
    {
        const double ActiveRestStepThreshold = 6000;
        const double CompleteRestStepThreshold = 1500;
        const double ActiveRestMinutesThreshold = 45;
        const double CompleteRestMinutesThreshold = 15;

        var activeRest = 0;
        var completeRest = 0;

        foreach (var day in steps)
        {
            var stepsValue = day.Steps;
            var minutes = activeMinutes.TryGetValue(day.Date, out var value) ? value : 0d;

            var isCompleteRest = stepsValue < CompleteRestStepThreshold && minutes < CompleteRestMinutesThreshold;
            if (isCompleteRest)
            {
                completeRest++;
                continue;
            }

            var isRestDay = stepsValue < ActiveRestStepThreshold && minutes < ActiveRestMinutesThreshold;
            if (isRestDay)
            {
                activeRest++;
            }
        }

        return new WeeklyRestSummary(
            Total: activeRest + completeRest,
            ActiveRest: activeRest,
            CompleteRest: completeRest);
    }

    private static WeeklyActivitySummary BuildWeeklyActivitySummary(ActivityProjection activity)
    {
        var durationMinutes = TimeSpan.FromTicks(activity.DurationTicks).TotalMinutes;
        var distanceKm = activity.DistanceMeters > 0 ? activity.DistanceMeters / 1000d : (double?)null;
        var type = string.IsNullOrWhiteSpace(activity.ActivityType) ? "Unknown" : activity.ActivityType.Trim();
        var intensity = ClassifyIntensity(type, distanceKm, durationMinutes);

        return new WeeklyActivitySummary(
            Type: type,
            DistanceKm: distanceKm,
            DurationMinutes: durationMinutes > 0 ? durationMinutes : null,
            Intensity: intensity);
    }

    private static RoutineEventSnapshot CreateSnapshot(RoutineEventToolkit.RoutineEventOccurrence occurrence)
    {
        var entity = occurrence.Event;
        var recurrenceDays = occurrence.RecurrenceDays.Count == 0
            ? Array.Empty<int>()
            : occurrence.RecurrenceDays.ToArray();

        return new RoutineEventSnapshot(
            Id: entity.Id,
            Title: entity.Title,
            Classification: entity.Classification,
            StartLocal: occurrence.StartLocal,
            EndLocal: occurrence.EndLocal,
            IsRace: entity.IsRace,
            IsRecurring: entity.IsRecurring,
            RecurrenceDays: recurrenceDays,
            RecurrenceStartDate: FormatIsoDate(entity.RecurrenceStartLocal),
            RecurrenceEndDate: FormatIsoDate(entity.RecurrenceEndLocal),
            RaceName: NormalizeText(entity.RaceName),
            RaceLocation: NormalizeText(entity.RaceLocation),
            RaceGoal: NormalizeText(entity.RaceGoal),
            Notes: NormalizeText(entity.Notes));
    }

    private static RaceEventInsight? CreateRaceInsight(RoutineEventToolkit.RoutineEventOccurrence occurrence, DateTime today, CalendarRecommendationOptions options)
    {
        var eventDate = DateOnly.FromDateTime(occurrence.StartLocal);
        var todayDate = DateOnly.FromDateTime(today);
        var daysOffset = eventDate.DayNumber - todayDate.DayNumber;

        var maxAhead = Math.Max(options.RaceLookaheadDays, options.RacePreparationWindowDays);
        if (daysOffset > maxAhead)
        {
            return null;
        }

        if (daysOffset < -options.RaceRecoveryWindowDays)
        {
            return null;
        }

        var phase = DetermineRacePhase(daysOffset, options);

        return new RaceEventInsight(
            Id: occurrence.Event.Id,
            Title: occurrence.Event.Title,
            RaceName: NormalizeText(occurrence.Event.RaceName),
            StartLocal: occurrence.StartLocal,
            EndLocal: occurrence.EndLocal,
            Classification: occurrence.Event.Classification,
            Phase: phase,
            DaysOffset: daysOffset,
            RaceLocation: NormalizeText(occurrence.Event.RaceLocation),
            RaceGoal: NormalizeText(occurrence.Event.RaceGoal),
            Notes: NormalizeText(occurrence.Event.Notes));
    }

    private static string DetermineRacePhase(int daysOffset, CalendarRecommendationOptions options)
    {
        if (daysOffset == 0)
        {
            return "race-day";
        }

        if (daysOffset > 0)
        {
            if (daysOffset <= options.RaceTaperWindowDays)
            {
                return "taper";
            }

            if (daysOffset <= options.RacePreparationWindowDays)
            {
                return "preparation";
            }

            return "future";
        }

        var daysSince = Math.Abs(daysOffset);
        if (daysSince <= options.RaceRecoveryWindowDays)
        {
            return "recovery";
        }

        return "past";
    }

    private static string? FormatIsoDate(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var local = value.Value.AsLocalTime();
        return DateOnly.FromDateTime(local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ClassifyIntensity(string activityType, double? distanceKm, double durationMinutes)
    {
        var normalizedType = activityType.ToLowerInvariant();

        if (normalizedType.Contains("yoga") || normalizedType.Contains("stretch") || normalizedType.Contains("meditation"))
        {
            return "low";
        }

        if (normalizedType.Contains("strength") || normalizedType.Contains("hiit") || normalizedType.Contains("interval"))
        {
            return durationMinutes >= 20 ? "high" : "moderate";
        }

        if (durationMinutes >= 60 || (distanceKm.HasValue && distanceKm.Value >= 12))
        {
            return "high";
        }

        if (durationMinutes >= 30 || (distanceKm.HasValue && distanceKm.Value >= 5))
        {
            return "moderate";
        }

        return "low";
    }

    private static double? ConvertSecondsToHours(double? seconds)
    {
        if (!seconds.HasValue)
        {
            return null;
        }

        return seconds.Value <= 0 ? 0 : seconds.Value / 3600d;
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
