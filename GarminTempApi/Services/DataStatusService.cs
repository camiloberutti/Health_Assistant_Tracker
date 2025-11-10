using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

public sealed record DataStatus(
    int ActivityCount,
    int SleepSummaryCount,
    int StepSummaryCount,
    int SleepDetailCount,
    DateTime? LatestActivityStart,
    DateTime? LatestSleepDate,
    DateTime? LatestStepDate,
    DateTime GeneratedUtc)
{
    public bool HasActivities => ActivityCount > 0;
    public bool HasSleepSummaries => SleepSummaryCount > 0;
    public bool HasStepSummaries => StepSummaryCount > 0;
    public bool HasSleepDetails => SleepDetailCount > 0;
    public bool IsReady => HasActivities && HasSleepSummaries && HasStepSummaries;

    public IReadOnlyList<string> MissingComponents => new List<string>
    {
        HasActivities ? null : "activities",
        HasSleepSummaries ? null : "sleep summaries",
        HasStepSummaries ? null : "step summaries"
    }.Where(component => component is not null)!
     .Select(component => component!)
     .ToList();
};

public class DataStatusService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DataStatusService> _logger;

    public DataStatusService(AppDbContext dbContext, ILogger<DataStatusService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<DataStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var activityCountTask = _dbContext.Activities.CountAsync(cancellationToken);
            var activityLatestTask = _dbContext.Activities
                .OrderByDescending(a => a.StartTime)
                .Select(a => (DateTime?)a.StartTime)
                .FirstOrDefaultAsync(cancellationToken);

            var sleepCountTask = _dbContext.SleepSummaries.CountAsync(cancellationToken);
            var sleepLatestTask = _dbContext.SleepSummaries
                .OrderByDescending(s => s.Date)
                .Select(s => (DateTime?)s.Date)
                .FirstOrDefaultAsync(cancellationToken);

            var sleepDetailTask = _dbContext.SleepDetailSnapshots.CountAsync(cancellationToken);

            var stepCountTask = _dbContext.StepSummaries.CountAsync(cancellationToken);
            var stepLatestTask = _dbContext.StepSummaries
                .OrderByDescending(s => s.Date)
                .Select(s => (DateTime?)s.Date)
                .FirstOrDefaultAsync(cancellationToken);

            await Task.WhenAll(activityCountTask, activityLatestTask, sleepCountTask, sleepLatestTask, sleepDetailTask, stepCountTask, stepLatestTask);

            return new DataStatus(
                ActivityCount: activityCountTask.Result,
                SleepSummaryCount: sleepCountTask.Result,
                StepSummaryCount: stepCountTask.Result,
                SleepDetailCount: sleepDetailTask.Result,
                LatestActivityStart: Normalize(activityLatestTask.Result),
                LatestSleepDate: NormalizeDate(sleepLatestTask.Result),
                LatestStepDate: NormalizeDate(stepLatestTask.Result),
                GeneratedUtc: DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute data status.");
            return new DataStatus(0, 0, 0, 0, null, null, null, DateTime.UtcNow);
        }
    }

    private static DateTime? Normalize(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
            _ => value.Value.ToUniversalTime()
        };
    }

    private static DateTime? NormalizeDate(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = Normalize(value);
        return normalized is null
            ? null
            : DateTime.SpecifyKind(normalized.Value.Date, DateTimeKind.Utc);
    }
}
