using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GarminTempApi.Data;
using GarminTempApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

public class GarminDataSyncService
{
    private static readonly JsonSerializerOptions DetailJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

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

        var detailLookup = fetchResult.Activities
            .Where(a => !string.IsNullOrWhiteSpace(a.ActivityId) && a.Detail is not null)
            .ToDictionary(a => a.ActivityId, a => a.Detail!, StringComparer.OrdinalIgnoreCase);

        var pendingSave = false;
        var activitiesInserted = 0;
        var sleepUpserted = 0;
        var stepUpserted = 0;

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
                if (string.IsNullOrWhiteSpace(activity.ActivityId) || existingIds.Contains(activity.ActivityId))
                {
                    continue;
                }

                if (activity.StartTime is null)
                {
                    _logger.LogWarning("Skipping activity {ActivityId} because StartTime is missing.", activity.ActivityId);
                    continue;
                }

                existingIds.Add(activity.ActivityId);

                var entity = new Activity
                {
                    ExternalId = activity.ActivityId,
                    Source = "garminconnect",
                    FileName = string.IsNullOrWhiteSpace(activity.Name) ? $"activity_{activity.ActivityId}" : activity.Name,
                    StartTime = activity.StartTime.Value,
                    DistanceMeters = activity.DistanceMeters ?? 0,
                    Duration = TimeSpan.FromSeconds(activity.DurationSeconds ?? 0),
                    ActivityType = string.IsNullOrWhiteSpace(activity.ActivityType) ? "unknown" : activity.ActivityType,
                };

                if (activity.Detail is { TrackPoints: { Count: > 0 } trackPoints })
                {
                    foreach (var point in BuildTrackPointsForNewActivity(entity, trackPoints))
                    {
                        entity.Points.Add(point);
                    }
                }

                newActivities.Add(entity);
            }

            if (newActivities.Count > 0)
            {
                await db.Activities.AddRangeAsync(newActivities, cancellationToken);
                activitiesInserted = newActivities.Count;
                pendingSave = true;
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
                    summary.SleepStartLocal = entry.SleepStartLocal;
                    summary.SleepEndLocal = entry.SleepEndLocal;
                    summary.SleepStartGmt = entry.SleepStartGmt is null ? null : NormalizeTimestamp(entry.SleepStartGmt.Value);
                    summary.SleepEndGmt = entry.SleepEndGmt is null ? null : NormalizeTimestamp(entry.SleepEndGmt.Value);
                    summary.RestingHeartRate = entry.RestingHeartRate;
                    summary.BodyBatteryChange = entry.BodyBatteryChange;
                    summary.AverageRespirationValue = entry.AverageRespirationValue;
                    summary.LowestSpO2Value = entry.LowestSpO2Value;
                    summary.SleepGoalSeconds = entry.SleepGoalSeconds;
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
                        SleepStartLocal = entry.SleepStartLocal,
                        SleepEndLocal = entry.SleepEndLocal,
                        SleepStartGmt = entry.SleepStartGmt is null ? null : NormalizeTimestamp(entry.SleepStartGmt.Value),
                        SleepEndGmt = entry.SleepEndGmt is null ? null : NormalizeTimestamp(entry.SleepEndGmt.Value),
                        RestingHeartRate = entry.RestingHeartRate,
                        BodyBatteryChange = entry.BodyBatteryChange,
                        AverageRespirationValue = entry.AverageRespirationValue,
                        LowestSpO2Value = entry.LowestSpO2Value,
                        SleepGoalSeconds = entry.SleepGoalSeconds,
                    };
                    await db.SleepSummaries.AddAsync(summary, cancellationToken);
                    existingSleep[dateKey] = summary;
                }

                sleepUpserted++;
            }

            if (sleepUpserted > 0)
            {
                pendingSave = true;
                _logger.LogInformation("Upserted {Count} sleep summaries.", sleepUpserted);
            }
        }

        if (fetchResult.SleepDetails.Count > 0)
        {
            var detailDates = fetchResult.SleepDetails
                .Select(d => d.Date.Date)
                .Distinct()
                .ToList();

            var existingDetails = await db.SleepDetailSnapshots
                .Where(d => detailDates.Contains(d.Date))
                .ToDictionaryAsync(d => d.Date.Date, cancellationToken);

            foreach (var detail in fetchResult.SleepDetails)
            {
                var dateKey = detail.Date.Date;
                var json = JsonSerializer.Serialize(detail, DetailJsonOptions);

                if (existingDetails.TryGetValue(dateKey, out var snapshot))
                {
                    if (!string.Equals(snapshot.DetailJson, json, StringComparison.Ordinal))
                    {
                        snapshot.DetailJson = json;
                        snapshot.LastUpdatedUtc = DateTime.UtcNow;
                        pendingSave = true;
                    }
                }
                else
                {
                    var entity = new SleepDetailSnapshot
                    {
                        Date = dateKey,
                        DetailJson = json,
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    await db.SleepDetailSnapshots.AddAsync(entity, cancellationToken);
                    existingDetails[dateKey] = entity;
                    pendingSave = true;
                }
            }
        }

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
                pendingSave = true;
                _logger.LogInformation("Upserted {Count} step summaries.", stepUpserted);
            }
        }

        if (pendingSave)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (detailLookup.Count > 0)
        {
            var externalIds = detailLookup.Keys.ToList();

            var activityIdMap = await db.Activities
                .Where(a => externalIds.Contains(a.ExternalId))
                .Select(a => new { a.Id, a.ExternalId })
                .ToDictionaryAsync(x => x.ExternalId, x => x.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

            if (activityIdMap.Count > 0)
            {
                await UpsertDetailSnapshotsAsync(db, detailLookup, activityIdMap, cancellationToken);
                await EnsureTrackPointsForExistingAsync(db, detailLookup, activityIdMap, cancellationToken);
            }
        }

        return new GarminSyncResult(
            activitiesInserted,
            fetchResult.Activities.Count,
            sleepUpserted,
            stepUpserted,
            fetchResult);
    }

    private static IEnumerable<ActivityPoint> BuildTrackPointsForNewActivity(Activity activity, IReadOnlyList<GarminTrackPoint> trackPoints)
    {
        if (trackPoints is null)
        {
            yield break;
        }

        foreach (var point in trackPoints)
        {
            if (point.Timestamp is null || point.Latitude is null || point.Longitude is null)
            {
                continue;
            }

            yield return new ActivityPoint
            {
                Activity = activity,
                Timestamp = NormalizeTimestamp(point.Timestamp.Value),
                Latitude = point.Latitude.Value,
                Longitude = point.Longitude.Value,
                Altitude = point.Altitude,
                HeartRate = point.HeartRate
            };
        }
    }

    private static IEnumerable<ActivityPoint> BuildTrackPointsForExistingActivity(int activityId, IReadOnlyList<GarminTrackPoint> trackPoints)
    {
        if (trackPoints is null)
        {
            yield break;
        }

        foreach (var point in trackPoints)
        {
            if (point.Timestamp is null || point.Latitude is null || point.Longitude is null)
            {
                continue;
            }

            yield return new ActivityPoint
            {
                ActivityId = activityId,
                Timestamp = NormalizeTimestamp(point.Timestamp.Value),
                Latitude = point.Latitude.Value,
                Longitude = point.Longitude.Value,
                Altitude = point.Altitude,
                HeartRate = point.HeartRate
            };
        }
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static async Task UpsertDetailSnapshotsAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, GarminActivityDetail> detailLookup,
        IReadOnlyDictionary<string, int> activityIdMap,
        CancellationToken cancellationToken)
    {
        var activityIds = activityIdMap.Values.ToList();

        var existingSnapshots = await db.ActivityDetailSnapshots
            .Where(s => activityIds.Contains(s.ActivityId))
            .ToDictionaryAsync(s => s.ActivityId, cancellationToken);

        var updated = false;

        foreach (var (externalId, detail) in detailLookup)
        {
            if (!activityIdMap.TryGetValue(externalId, out var activityId))
            {
                continue;
            }

            var json = JsonSerializer.Serialize(detail, DetailJsonOptions);
            if (existingSnapshots.TryGetValue(activityId, out var snapshot))
            {
                if (!string.Equals(snapshot.DetailJson, json, StringComparison.Ordinal))
                {
                    snapshot.DetailJson = json;
                    snapshot.LastUpdatedUtc = DateTime.UtcNow;
                    updated = true;
                }
            }
            else
            {
                await db.ActivityDetailSnapshots.AddAsync(new ActivityDetailSnapshot
                {
                    ActivityId = activityId,
                    DetailJson = json,
                    LastUpdatedUtc = DateTime.UtcNow
                }, cancellationToken);
                updated = true;
            }
        }

        if (updated)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task EnsureTrackPointsForExistingAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, GarminActivityDetail> detailLookup,
        IReadOnlyDictionary<string, int> activityIdMap,
        CancellationToken cancellationToken)
    {
        var activityIds = activityIdMap.Values.ToList();
        if (activityIds.Count == 0)
        {
            return;
        }

        var activitiesWithPoints = await db.ActivityPoints
            .Where(p => activityIds.Contains(p.ActivityId))
            .GroupBy(p => p.ActivityId)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        var existingPointSet = activitiesWithPoints.ToHashSet();

        var newPoints = new List<ActivityPoint>();

        foreach (var (externalId, detail) in detailLookup)
        {
            if (!activityIdMap.TryGetValue(externalId, out var activityId))
            {
                continue;
            }

            if (existingPointSet.Contains(activityId))
            {
                continue;
            }

            newPoints.AddRange(BuildTrackPointsForExistingActivity(activityId, detail.TrackPoints));
        }

        if (newPoints.Count > 0)
        {
            await db.ActivityPoints.AddRangeAsync(newPoints, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

public record GarminSyncResult(
    int ActivitiesInserted,
    int ActivitiesFetched,
    int SleepUpserted,
    int StepsUpserted,
    GarminConnectFetchResult FetchResult
);
