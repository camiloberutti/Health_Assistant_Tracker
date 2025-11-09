using GarminTempApi.Data;
using GarminTempApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

public class GarminDataSyncService
{
    private readonly GarminConnectImporter _importer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GarminDataSyncService> _logger;

    public GarminDataSyncService(GarminConnectImporter importer, IServiceScopeFactory scopeFactory, ILogger<GarminDataSyncService> logger)
    {
        _importer = importer;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<GarminSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!_importer.HasCredentials())
        {
            throw new InvalidOperationException("Garmin credentials are not configured.");
        }

        var fetchResult = await _importer.FetchActivitiesAsync(cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var changes = false;

        var activitiesInserted = 0;
        if (fetchResult.Activities.Count > 0)
        {
            var activityIds = fetchResult.Activities
                .Where(a => !string.IsNullOrWhiteSpace(a.ActivityId))
                .Select(a => a.ActivityId)
                .ToList();

            var existingIds = (await db.Activities
                .Where(a => activityIds.Contains(a.ExternalId))
                .Select(a => a.ExternalId)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newActivities = new List<Activity>();
            foreach (var activity in fetchResult.Activities)
            {
                if (string.IsNullOrWhiteSpace(activity.ActivityId))
                {
                    continue;
                }

                if (existingIds.Contains(activity.ActivityId))
                {
                    continue;
                }

                if (activity.StartTime is null)
                {
                    _logger.LogWarning("Skipping activity {ActivityId} because StartTime is missing.", activity.ActivityId);
                    continue;
                }

                existingIds.Add(activity.ActivityId);

                newActivities.Add(new Activity
                {
                    ExternalId = activity.ActivityId,
                    Source = "garminconnect",
                    FileName = string.IsNullOrWhiteSpace(activity.Name) ? $"activity_{activity.ActivityId}" : activity.Name,
                    StartTime = activity.StartTime.Value,
                    DistanceMeters = activity.DistanceMeters ?? 0,
                    Duration = TimeSpan.FromSeconds(activity.DurationSeconds ?? 0),
                    ActivityType = string.IsNullOrWhiteSpace(activity.ActivityType) ? "unknown" : activity.ActivityType,
                });
            }

            if (newActivities.Count > 0)
            {
                await db.Activities.AddRangeAsync(newActivities, cancellationToken);
                activitiesInserted = newActivities.Count;
                changes = true;
                _logger.LogInformation("Stored {Count} new Garmin activities.", newActivities.Count);
            }
            else
            {
                _logger.LogInformation("All fetched activities already exist in the local database.");
            }
        }
        else
        {
            _logger.LogInformation("Garmin sync fetched zero activities.");
        }

        var sleepUpserted = 0;
        if (fetchResult.Sleep.Count > 0)
        {
            var minDate = fetchResult.Sleep.Min(s => s.Date.Date);
            var maxDate = fetchResult.Sleep.Max(s => s.Date.Date);

            var existingSleep = await db.SleepSummaries
                .Where(s => s.Date >= minDate && s.Date <= maxDate)
                .ToDictionaryAsync(s => s.Date.Date, cancellationToken);

            foreach (var entry in fetchResult.Sleep)
            {
                var dateKey = entry.Date.Date;
                if (existingSleep.TryGetValue(dateKey, out var summary))
                {
                    summary.TotalSleepSeconds = entry.TotalSleepSeconds ?? 0;
                    summary.DeepSleepSeconds = entry.DeepSleepSeconds ?? 0;
                    summary.LightSleepSeconds = entry.LightSleepSeconds ?? 0;
                    summary.RemSleepSeconds = entry.RemSleepSeconds ?? 0;
                    summary.AwakeSeconds = entry.AwakeSeconds ?? 0;
                    summary.SleepScore = entry.SleepScore;
                    summary.SleepQualityType = entry.SleepQualityType;
                }
                else
                {
                    summary = new SleepSummary
                    {
                        Date = dateKey,
                        TotalSleepSeconds = entry.TotalSleepSeconds ?? 0,
                        DeepSleepSeconds = entry.DeepSleepSeconds ?? 0,
                        LightSleepSeconds = entry.LightSleepSeconds ?? 0,
                        RemSleepSeconds = entry.RemSleepSeconds ?? 0,
                        AwakeSeconds = entry.AwakeSeconds ?? 0,
                        SleepScore = entry.SleepScore,
                        SleepQualityType = entry.SleepQualityType,
                    };
                    await db.SleepSummaries.AddAsync(summary, cancellationToken);
                    existingSleep[dateKey] = summary;
                }

                sleepUpserted++;
            }

            if (sleepUpserted > 0)
            {
                changes = true;
                _logger.LogInformation("Upserted {Count} sleep summaries.", sleepUpserted);
            }
        }

        var stepUpserted = 0;
        if (fetchResult.Steps.Count > 0)
        {
            var minDate = fetchResult.Steps.Min(s => s.Date.Date);
            var maxDate = fetchResult.Steps.Max(s => s.Date.Date);

            var existingSteps = await db.StepSummaries
                .Where(s => s.Date >= minDate && s.Date <= maxDate)
                .ToDictionaryAsync(s => s.Date.Date, cancellationToken);

            foreach (var entry in fetchResult.Steps)
            {
                var dateKey = entry.Date.Date;
                if (existingSteps.TryGetValue(dateKey, out var summary))
                {
                    summary.TotalSteps = entry.TotalSteps ?? 0;
                    summary.GoalSteps = entry.GoalSteps ?? 0;
                    summary.TotalCalories = entry.TotalCalories ?? 0;
                    summary.ActiveCalories = entry.ActiveCalories ?? 0;
                    summary.TotalDistanceMeters = entry.TotalDistanceMeters ?? 0;
                }
                else
                {
                    summary = new StepSummary
                    {
                        Date = dateKey,
                        TotalSteps = entry.TotalSteps ?? 0,
                        GoalSteps = entry.GoalSteps ?? 0,
                        TotalCalories = entry.TotalCalories ?? 0,
                        ActiveCalories = entry.ActiveCalories ?? 0,
                        TotalDistanceMeters = entry.TotalDistanceMeters ?? 0,
                    };
                    await db.StepSummaries.AddAsync(summary, cancellationToken);
                    existingSteps[dateKey] = summary;
                }

                stepUpserted++;
            }

            if (stepUpserted > 0)
            {
                changes = true;
                _logger.LogInformation("Upserted {Count} step summaries.", stepUpserted);
            }
        }

        if (changes)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new GarminSyncResult(
            activitiesInserted,
            fetchResult.Activities.Count,
            sleepUpserted,
            stepUpserted,
            fetchResult);
    }
}

public record GarminSyncResult(
    int ActivitiesInserted,
    int ActivitiesFetched,
    int SleepUpserted,
    int StepsUpserted,
    GarminConnectFetchResult FetchResult
);
