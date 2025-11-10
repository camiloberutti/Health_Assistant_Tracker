using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using GarminTempApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarminTempApi.Tests;

public class InsightDataBuilderTests
{
    [Fact]
    public async Task BuildUserDataDigestAsync_ComputesAggregations()
    {
        await using var context = CreateContext();
        await SeedSampleDataAsync(context);

        var builder = new InsightDataBuilder(context, NullLogger<InsightDataBuilder>.Instance);
        var digest = await builder.BuildUserDataDigestAsync(new DateTime(2025, 11, 8), new DateTime(2025, 11, 9), CancellationToken.None);

        Assert.Equal(new DateTime(2025, 11, 8), digest.RangeStart);
        Assert.Equal(new DateTime(2025, 11, 9), digest.RangeEnd);
        Assert.Equal(2, digest.DailyStats.Count);
        Assert.Equal(25d, Math.Round(digest.TotalDistanceKm, 2));
        Assert.Equal(90d, Math.Round(digest.TotalActiveMinutes, 2));
        Assert.Equal(9000d, digest.AverageSteps);
        Assert.Equal(28500d, digest.AverageSleepSeconds);
        Assert.Equal(52d, digest.AverageRestingHeartRate);

        var firstDay = digest.DailyStats.First();
        Assert.Equal("Run", Assert.Single(firstDay.ActivityTypes));
        Assert.Equal(5000d / 1000d, firstDay.DistanceKm);
        Assert.Equal(30d, firstDay.ActiveMinutes);
    }

    [Fact]
    public async Task BuildDailyRecommendationContext_UsesLookbackWindow()
    {
        await using var context = CreateContext();
        await SeedSampleDataAsync(context);

        var builder = new InsightDataBuilder(context, NullLogger<InsightDataBuilder>.Instance);
        var digest = await builder.BuildUserDataDigestAsync(new DateTime(2025, 11, 1), new DateTime(2025, 11, 9), CancellationToken.None);
        var contextForDay = builder.BuildDailyRecommendationContext(new DateTime(2025, 11, 9), digest);

        Assert.Equal(new DateTime(2025, 11, 9), contextForDay.TargetDate);
        Assert.Equal(10000d, contextForDay.Steps);
        Assert.Equal(9000d, contextForDay.StepsAverage);
        Assert.Equal(60d, contextForDay.ActiveMinutes);
        Assert.Equal(45d, contextForDay.ActiveMinutesAverage);
        Assert.Equal(20d, contextForDay.DistanceKm);
        Assert.Equal(12.5d, contextForDay.DistanceKmAverage);
        Assert.Equal(30000d, contextForDay.SleepSeconds);
        Assert.Equal(28500d, contextForDay.SleepSecondsAverage);
        Assert.Equal(10d, contextForDay.BodyBatteryChange);
        Assert.Equal(50d, contextForDay.RestingHeartRate);

        Assert.Equal(new[] { "Cycling", "Run" }, contextForDay.DominantActivityTypes.ToArray());
    }

    private static async Task SeedSampleDataAsync(AppDbContext context)
    {
        var dayOne = new DateTime(2025, 11, 8);
        var dayTwo = new DateTime(2025, 11, 9);

        context.StepSummaries.AddRange(
            new StepSummary { Date = dayOne, TotalSteps = 8000 },
            new StepSummary { Date = dayTwo, TotalSteps = 10000 }
        );

        context.SleepSummaries.AddRange(
            new SleepSummary { Date = dayOne, TotalSleepSeconds = 27000, BodyBatteryChange = 12, RestingHeartRate = 54 },
            new SleepSummary { Date = dayTwo, TotalSleepSeconds = 30000, BodyBatteryChange = 10, RestingHeartRate = 50 }
        );

        context.Activities.AddRange(
            new Activity
            {
                StartTime = dayOne.AddHours(6),
                DistanceMeters = 5000,
                Duration = TimeSpan.FromMinutes(30),
                ActivityType = "Run"
            },
            new Activity
            {
                StartTime = dayTwo.AddHours(7),
                DistanceMeters = 20000,
                Duration = TimeSpan.FromMinutes(60),
                ActivityType = "Cycling"
            }
        );

        await context.SaveChangesAsync();
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"InsightDataBuilderTests_{Guid.NewGuid()}")
            .Options;

        return new AppDbContext(options);
    }
}
