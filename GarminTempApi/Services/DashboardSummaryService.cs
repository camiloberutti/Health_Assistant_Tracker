using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

public sealed record DashboardCardStat(string Label, string Value);

public sealed record DashboardCard(
    string Title,
    string PrimaryValue,
    string? PrimaryCaption,
    string? Subtitle,
    string DestinationUrl,
    string IconCss,
    string AccentCss,
    IReadOnlyList<DashboardCardStat> Stats);

public sealed record DashboardSummary(
    IReadOnlyList<DashboardCard> Cards,
    DateTime GeneratedUtc,
    DateOnly? LatestDataDate);

public class DashboardSummaryService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DashboardSummaryService> _logger;

    public DashboardSummaryService(AppDbContext dbContext, ILogger<DashboardSummaryService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var cards = new List<DashboardCard>();
        var latestDates = new List<DateOnly>();

        try
        {
            var stepsDate = await BuildStepsCardAsync(cards, cancellationToken);
            if (stepsDate is DateOnly stepLatest)
            {
                latestDates.Add(stepLatest);
            }

            var sleepDate = await BuildSleepCardAsync(cards, cancellationToken);
            if (sleepDate is DateOnly sleepLatest)
            {
                latestDates.Add(sleepLatest);
            }

            var activityDate = await BuildActivityCardAsync(cards, cancellationToken);
            if (activityDate is DateOnly activityLatest)
            {
                latestDates.Add(activityLatest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build dashboard summary.");
        }

        var latestDate = latestDates.Count == 0 ? (DateOnly?)null : latestDates.Max();

        return new DashboardSummary(cards, DateTime.UtcNow, latestDate);
    }

    private async Task<DateOnly?> BuildStepsCardAsync(ICollection<DashboardCard> cards, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.StepSummaries
            .OrderByDescending(s => s.Date)
            .Select(s => new
            {
                s.Date,
                s.TotalSteps,
                s.GoalSteps,
                s.TotalDistanceMeters
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return null;
        }

        var weekStart = latest.Date.AddDays(-6);
        var weeklyTotal = await _dbContext.StepSummaries
            .Where(s => s.Date >= weekStart && s.Date <= latest.Date)
            .SumAsync(s => s.TotalSteps, cancellationToken);

        var percentOfGoal = latest.GoalSteps > 0
            ? (latest.TotalSteps / latest.GoalSteps)
            : (double?)null;

        var stats = new List<DashboardCardStat>
        {
            new("Goal", $"{latest.GoalSteps:N0} steps"),
            new("Distance", $"{latest.TotalDistanceMeters / 1000:0.0} km")
        };

        if (weeklyTotal > 0)
        {
            stats.Add(new DashboardCardStat("7-day total", $"{weeklyTotal:N0} steps"));
        }

        var subtitle = percentOfGoal is null
            ? "No goal set"
            : string.Format(CultureInfo.InvariantCulture, "{0:P0} of goal", percentOfGoal.Value);

        cards.Add(new DashboardCard(
            Title: "Steps",
            PrimaryValue: string.Format(CultureInfo.InvariantCulture, "{0:N0}", latest.TotalSteps),
            PrimaryCaption: latest.Date.ToString("ddd, dd MMM yy", CultureInfo.InvariantCulture),
            Subtitle: subtitle,
            DestinationUrl: "/Steps",
            IconCss: "icon-steps",
            AccentCss: "accent-steps",
            Stats: stats));

        return DateOnly.FromDateTime(latest.Date);
    }

    private async Task<DateOnly?> BuildSleepCardAsync(ICollection<DashboardCard> cards, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.SleepSummaries
            .OrderByDescending(s => s.Date)
            .Select(s => new
            {
                s.Date,
                s.TotalSleepSeconds,
                s.DeepSleepSeconds,
                s.RemSleepSeconds,
                s.SleepScore,
                s.RestingHeartRate,
                s.BodyBatteryChange
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return null;
        }

        var totalSleep = TimeSpan.FromSeconds(latest.TotalSleepSeconds);
        var deepPlusRem = TimeSpan.FromSeconds(latest.DeepSleepSeconds + latest.RemSleepSeconds);

        var rollingWindowEnd = latest.Date;
        var rollingWindowStart = rollingWindowEnd.AddDays(-6);

        var recentRestingHr = await _dbContext.SleepSummaries
            .Where(s => s.Date >= rollingWindowStart && s.Date <= rollingWindowEnd && s.RestingHeartRate != null)
            .Select(s => s.RestingHeartRate!.Value)
            .ToListAsync(cancellationToken);

        double? averageRestingHr = recentRestingHr.Count == 0 ? null : recentRestingHr.Average();

        var stats = new List<DashboardCardStat>
        {
            new("Sleep score", latest.SleepScore is null ? "--" : latest.SleepScore.Value.ToString("0", CultureInfo.InvariantCulture)),
            new("Deep + REM", FormatDuration(deepPlusRem))
        };

        if (averageRestingHr.HasValue)
        {
            stats.Add(new DashboardCardStat("Avg resting HR", $"{averageRestingHr.Value:0} bpm"));
        }

        cards.Add(new DashboardCard(
            Title: "Sleep",
            PrimaryValue: FormatDuration(totalSleep),
            PrimaryCaption: latest.Date.ToString("dd MMM yy", CultureInfo.InvariantCulture),
            Subtitle: latest.BodyBatteryChange is null
                ? "Nightly recovery overview"
                : string.Format(CultureInfo.InvariantCulture, "Body Battery {0:+#;-#;0}", latest.BodyBatteryChange.Value),
            DestinationUrl: "/Sleep",
            IconCss: "bi-moon-stars",
            AccentCss: "accent-sleep",
            Stats: stats));

        return DateOnly.FromDateTime(latest.Date);
    }

    private async Task<DateOnly?> BuildActivityCardAsync(ICollection<DashboardCard> cards, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.Activities
            .OrderByDescending(a => a.StartTime)
            .Select(a => new
            {
                a.StartTime,
                a.DistanceMeters,
                a.Duration,
                a.ActivityType
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return null;
        }

        var since = DateTime.UtcNow.AddDays(-6);
        var recentActivities = await _dbContext.Activities
            .Where(a => a.StartTime >= since)
            .Select(a => new { a.DistanceMeters, a.Duration })
            .ToListAsync(cancellationToken);

        var totalDistanceMeters = recentActivities.Sum(a => a.DistanceMeters);
        var totalDuration = recentActivities.Aggregate(TimeSpan.Zero, (current, entry) => current + entry.Duration);

        var stats = new List<DashboardCardStat>
        {
            new("Latest", BuildLatestActivitySummary(latest.DistanceMeters, latest.Duration, latest.ActivityType)),
            new("7-day distance", $"{totalDistanceMeters / 1000:0.0} km")
        };

        if (totalDuration > TimeSpan.Zero)
        {
            stats.Add(new DashboardCardStat("7-day time", FormatDuration(totalDuration)));
        }

        cards.Add(new DashboardCard(
            Title: "Activities",
            PrimaryValue: BuildPrimaryActivityValue(latest.DistanceMeters, latest.Duration),
            PrimaryCaption: latest.StartTime.ToLocalTime().ToString("ddd, dd MMM yy", CultureInfo.InvariantCulture),
            Subtitle: string.IsNullOrWhiteSpace(latest.ActivityType) ? "Most recent workout" : latest.ActivityType,
            DestinationUrl: "/Activities",
            IconCss: "bi-activity",
            AccentCss: "accent-activity",
            Stats: stats));

        var latestLocal = latest.StartTime;

        if (latestLocal.Kind == DateTimeKind.Utc)
        {
            latestLocal = latestLocal.ToLocalTime();
        }
        else if (latestLocal.Kind == DateTimeKind.Unspecified)
        {
            latestLocal = DateTime.SpecifyKind(latestLocal, DateTimeKind.Local);
        }

        return DateOnly.FromDateTime(latestLocal);
    }

    private static string BuildPrimaryActivityValue(double distanceMeters, TimeSpan duration)
    {
        if (distanceMeters > 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} km", distanceMeters / 1000);
        }

        return FormatDuration(duration);
    }

    private static string BuildLatestActivitySummary(double distanceMeters, TimeSpan duration, string activityType)
    {
        var segments = new List<string>();
        if (!string.IsNullOrWhiteSpace(activityType))
        {
            segments.Add(activityType);
        }

        if (distanceMeters > 0)
        {
            segments.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} km", distanceMeters / 1000));
        }

        if (duration > TimeSpan.Zero)
        {
            segments.Add(FormatDuration(duration));
        }

        var pace = CalculatePace(distanceMeters, duration);
        if (!string.IsNullOrEmpty(pace))
        {
            segments.Add(pace);
        }

        return segments.Count == 0 ? "--" : string.Join(" · ", segments);
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0m";
        }

        var builder = new List<string>();

        if (value.TotalHours >= 1)
        {
            builder.Add(((int)value.TotalHours).ToString(CultureInfo.InvariantCulture) + "h");
        }

        var minutes = value.Minutes;
        if (minutes > 0)
        {
            builder.Add(minutes.ToString(CultureInfo.InvariantCulture) + "m");
        }

        var seconds = value.Seconds;
        if (builder.Count == 0 && seconds > 0)
        {
            builder.Add(seconds.ToString(CultureInfo.InvariantCulture) + "s");
        }

        return builder.Count == 0 ? "0m" : string.Join(" ", builder);
    }

    private static string? CalculatePace(double distanceMeters, TimeSpan duration)
    {
        if (distanceMeters <= 0 || duration.TotalSeconds <= 0)
        {
            return null;
        }

        var paceSeconds = duration.TotalSeconds / (distanceMeters / 1000);
        var paceTime = TimeSpan.FromSeconds(paceSeconds);
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}/km", (int)paceTime.TotalMinutes, paceTime.Seconds);
    }
}