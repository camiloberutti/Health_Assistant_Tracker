using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace GarminTempApi.Plugins;

/// <summary>
/// Semantic Kernel plugin that exposes the user's Garmin health data to the AI.
/// The AI can call these functions to query specific data rather than receiving
/// a massive context dump.
/// </summary>
public sealed class HealthDataPlugin
{
    private readonly IServiceProvider _serviceProvider;

    public HealthDataPlugin(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [KernelFunction("get_recent_activities")]
    [Description("Retrieves the user's recent activities (runs, swims, cycling, strength, etc.) for a given number of days back from today. Returns activity type, distance in km, duration in minutes, and date. Optionally filter by activity type.")]
    public async Task<string> GetRecentActivitiesAsync(
        [Description("Number of days to look back from today. Default is 7.")] int days = 7,
        [Description("Optional activity type filter, e.g. 'running', 'swimming', 'cycling'. Leave empty for all types.")] string? activityType = null,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var since = DateTime.UtcNow.Date.AddDays(-days);
        var query = db.Activities.Where(a => a.StartTime >= since);

        if (!string.IsNullOrWhiteSpace(activityType))
            query = query.Where(a => a.ActivityType.ToLower().Contains(activityType.ToLower()));

        var activities = await query
            .OrderByDescending(a => a.StartTime)
            .Select(a => new
            {
                date = a.StartTime.ToString("yyyy-MM-dd"),
                type = a.ActivityType,
                distanceKm = Math.Round(a.DistanceMeters / 1000.0, 2),
                durationMinutes = Math.Round(a.Duration.TotalMinutes, 1)
            })
            .ToListAsync(cancellationToken);

        if (activities.Count == 0)
            return string.IsNullOrWhiteSpace(activityType)
                ? $"No activities found in the last {days} days."
                : $"No '{activityType}' activities found in the last {days} days.";

        return JsonSerializer.Serialize(activities, new JsonSerializerOptions { WriteIndented = false });
    }

    [KernelFunction("get_sleep_summary")]
    [Description("Retrieves the user's comprehensive sleep data for a given number of days. Returns total/deep/light/REM/awake sleep hours, sleep score, quality type, bedtime and wake time, resting heart rate, body battery change, SpO2, respiration rate, and sleep goal.")]
    public async Task<string> GetSleepSummaryAsync(
        [Description("Number of days to look back from today. Default is 7.")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var since = DateTime.UtcNow.Date.AddDays(-days);
        var summaries = await db.SleepSummaries
            .Where(s => s.Date >= since)
            .OrderByDescending(s => s.Date)
            .ToListAsync(cancellationToken);

        if (summaries.Count == 0)
            return $"No sleep data found in the last {days} days.";

        var result = summaries.Select(s => new
        {
            date = s.Date.ToString("yyyy-MM-dd"),
            totalSleepHours = Math.Round(s.TotalSleepSeconds / 3600.0, 2),
            deepSleepHours = Math.Round(s.DeepSleepSeconds / 3600.0, 2),
            lightSleepHours = Math.Round(s.LightSleepSeconds / 3600.0, 2),
            remSleepHours = Math.Round(s.RemSleepSeconds / 3600.0, 2),
            awakeHours = Math.Round(s.AwakeSeconds / 3600.0, 2),
            sleepScore = s.SleepScore,
            sleepQuality = s.SleepQualityType,
            bedtime = s.SleepStartLocal?.ToString("HH:mm"),
            wakeTime = s.SleepEndLocal?.ToString("HH:mm"),
            restingHeartRate = s.RestingHeartRate,
            bodyBatteryChange = s.BodyBatteryChange,
            avgRespiration = s.AverageRespirationValue,
            lowestSpO2 = s.LowestSpO2Value,
            sleepGoalHours = s.SleepGoalSeconds.HasValue ? Math.Round(s.SleepGoalSeconds.Value / 3600.0, 2) : (double?)null
        }).ToList();

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
    }

    [KernelFunction("get_step_summary")]
    [Description("Retrieves the user's daily step counts, distance, and calorie data for a given number of days.")]
    public async Task<string> GetStepSummaryAsync(
        [Description("Number of days to look back from today. Default is 7.")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var since = DateTime.UtcNow.Date.AddDays(-days);
        var steps = await db.StepSummaries
            .Where(s => s.Date >= since)
            .OrderByDescending(s => s.Date)
            .Select(s => new
            {
                date = s.Date.ToString("yyyy-MM-dd"),
                steps = s.TotalSteps,
                goalSteps = s.GoalSteps,
                distanceKm = Math.Round(s.TotalDistanceMeters / 1000.0, 2),
                totalCalories = s.TotalCalories,
                activeCalories = s.ActiveCalories
            })
            .ToListAsync(cancellationToken);

        if (steps.Count == 0)
            return $"No step data found in the last {days} days.";

        return JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = false });
    }

    [KernelFunction("get_training_load_analysis")]
    [Description("Analyzes the user's training load over a period. Returns total distance, total active minutes, average daily metrics, rest day count, and whether fatigue is accumulating or the user is recovering. Use this to decide if the user should train hard or rest.")]
    public async Task<string> GetTrainingLoadAnalysisAsync(
        [Description("Number of days to analyze. Default is 14.")] int days = 14,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 3, 90);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var since = DateTime.UtcNow.Date.AddDays(-days);
        var endDate = DateTime.UtcNow.Date;

        var activities = await db.Activities
            .Where(a => a.StartTime >= since && a.StartTime <= endDate)
            .ToListAsync(cancellationToken);

        var sleepData = await db.SleepSummaries
            .Where(s => s.Date >= since && s.Date <= endDate)
            .ToListAsync(cancellationToken);

        var stepData = await db.StepSummaries
            .Where(s => s.Date >= since && s.Date <= endDate)
            .ToListAsync(cancellationToken);

        var totalDistanceKm = activities.Sum(a => a.DistanceMeters / 1000.0);
        var totalActiveMinutes = activities.Sum(a => a.Duration.TotalMinutes);
        var activityDayCount = activities.Select(a => a.StartTime.Date).Distinct().Count();
        var restDays = days - activityDayCount;

        var avgSleepHours = sleepData.Count > 0
            ? sleepData.Average(s => s.TotalSleepSeconds / 3600.0) : (double?)null;
        var avgRhr = sleepData.Where(s => s.RestingHeartRate.HasValue).Select(s => s.RestingHeartRate!.Value).DefaultIfEmpty().Average();

        // Trend: compare last 3 days RHR vs overall avg
        var last3Rhr = sleepData
            .OrderByDescending(s => s.Date).Take(3)
            .Where(s => s.RestingHeartRate.HasValue)
            .Select(s => s.RestingHeartRate!.Value).DefaultIfEmpty().Average();

        var rhrTrend = avgRhr > 0 && last3Rhr > 0
            ? last3Rhr > avgRhr + 3 ? "rising (possible fatigue)" : last3Rhr < avgRhr - 2 ? "dropping (good recovery)" : "stable"
            : "insufficient data";

        var avgSteps = stepData.Count > 0 ? stepData.Average(s => s.TotalSteps) : (double?)null;

        var activityBreakdown = activities
            .GroupBy(a => a.ActivityType)
            .Select(g => new { type = g.Key, count = g.Count(), totalDistanceKm = Math.Round(g.Sum(a => a.DistanceMeters / 1000.0), 2) })
            .OrderByDescending(x => x.count)
            .ToList();

        var analysis = new
        {
            periodDays = days,
            totalDistanceKm = Math.Round(totalDistanceKm, 2),
            totalActiveMinutes = Math.Round(totalActiveMinutes, 1),
            activeDays = activityDayCount,
            restDays,
            avgSleepHours = avgSleepHours.HasValue ? Math.Round(avgSleepHours.Value, 2) : (double?)null,
            avgRestingHeartRate = Math.Round(avgRhr, 1),
            restingHeartRateTrend = rhrTrend,
            avgDailySteps = avgSteps.HasValue ? Math.Round(avgSteps.Value, 0) : (double?)null,
            activityBreakdown
        };

        return JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = false });
    }

    [KernelFunction("get_body_battery_trend")]
    [Description("Retrieves the user's body battery change trend over a period. Body battery indicates recovery vs drain. Positive values = recharged during sleep, negative = drained. Use this to assess readiness for intense training.")]
    public async Task<string> GetBodyBatteryTrendAsync(
        [Description("Number of days to look back from today. Default is 7.")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var since = DateTime.UtcNow.Date.AddDays(-days);
        var data = await db.SleepSummaries
            .Where(s => s.Date >= since && s.BodyBatteryChange.HasValue)
            .OrderByDescending(s => s.Date)
            .Select(s => new
            {
                date = s.Date.ToString("yyyy-MM-dd"),
                bodyBatteryChange = s.BodyBatteryChange,
                sleepScore = s.SleepScore,
                restingHeartRate = s.RestingHeartRate
            })
            .ToListAsync(cancellationToken);

        if (data.Count == 0)
            return $"No body battery data found in the last {days} days.";

        var avgChange = data.Average(d => d.bodyBatteryChange ?? 0);
        var trend = avgChange > 30 ? "excellent recovery" : avgChange > 15 ? "good recovery"
            : avgChange > 0 ? "moderate recovery" : "poor recovery (draining)";

        var last3Avg = data.Take(3).Average(d => d.bodyBatteryChange ?? 0);
        var direction = last3Avg > avgChange + 5 ? "improving" : last3Avg < avgChange - 5 ? "declining" : "stable";

        var result = new
        {
            periodDays = days,
            dailyData = data,
            averageBatteryChange = Math.Round(avgChange, 1),
            recoveryAssessment = trend,
            recentDirection = direction
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
    }

    [KernelFunction("get_activity_details")]
    [Description("Retrieves detailed information for a specific recent activity, including the raw detail snapshot (HR zones, splits, cadence, etc.) if available. Use the activity date and type to find it.")]
    public async Task<string> GetActivityDetailsAsync(
        [Description("The date of the activity in yyyy-MM-dd format")] string date,
        [Description("Optional activity type filter, e.g. 'running', 'swimming'. Leave empty to get the first activity on that date.")] string? activityType = null,
        CancellationToken cancellationToken = default)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            return $"Error: Invalid date format '{date}'. Use yyyy-MM-dd.";

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dayStart = parsedDate.Date;
        var dayEnd = dayStart.AddDays(1);

        var query = db.Activities
            .Include(a => a.DetailSnapshot)
            .Where(a => a.StartTime >= dayStart && a.StartTime < dayEnd);

        if (!string.IsNullOrWhiteSpace(activityType))
            query = query.Where(a => a.ActivityType.ToLower().Contains(activityType.ToLower()));

        var activity = await query.FirstOrDefaultAsync(cancellationToken);

        if (activity == null)
            return string.IsNullOrWhiteSpace(activityType)
                ? $"No activity found on {date}."
                : $"No '{activityType}' activity found on {date}.";

        var result = new
        {
            date = activity.StartTime.ToString("yyyy-MM-dd HH:mm"),
            type = activity.ActivityType,
            distanceKm = Math.Round(activity.DistanceMeters / 1000.0, 2),
            durationMinutes = Math.Round(activity.Duration.TotalMinutes, 1),
            source = activity.Source,
            hasDetailSnapshot = activity.DetailSnapshot != null,
            detailSnapshot = activity.DetailSnapshot?.DetailJson
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
    }
}
