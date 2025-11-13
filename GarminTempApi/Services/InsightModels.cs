using System;
using System.Collections.Generic;

namespace GarminTempApi.Services;

public record DailyRecommendationContext(
    DateTime TargetDate,
    double? Steps,
    double? StepsAverage,
    double? ActiveMinutes,
    double? ActiveMinutesAverage,
    double? DistanceKm,
    double? DistanceKmAverage,
    double? SleepSeconds,
    double? SleepSecondsAverage,
    double? BodyBatteryChange,
    double? RestingHeartRate,
    IReadOnlyList<string> DominantActivityTypes
);

public record UserDataDigest(
    DateTime RangeStart,
    DateTime RangeEnd,
    IReadOnlyList<UserDailyStat> DailyStats,
    double TotalDistanceKm,
    double TotalActiveMinutes,
    double? AverageSleepSeconds,
    double? AverageSteps,
    double? AverageRestingHeartRate
);

public record UserDailyStat(
    DateTime Date,
    double? Steps,
    double? SleepSeconds,
    double? ActiveMinutes,
    double? DistanceKm,
    double? BodyBatteryChange,
    double? RestingHeartRate,
    IReadOnlyList<string> ActivityTypes
);

public record WeeklyHealthSnapshot(
    DateOnly RangeStart,
    DateOnly RangeEnd,
    WeeklyStepSummary Steps,
    WeeklySleepSummary Sleep,
    WeeklyRestSummary RestDays,
    IReadOnlyList<WeeklyActivitySummary> Activities,
    double? TotalCaloriesBurned
);

public record WeeklyStepSummary(
    IReadOnlyList<WeeklyStepDay> Daily,
    double TotalDistanceKm,
    double? AverageSteps
);

public record WeeklyStepDay(DateOnly Date, double Steps);

public record WeeklySleepSummary(
    double? AverageHours,
    double? DeepSleepHours,
    double? RemSleepHours,
    double? AverageSleepScore
);

public record WeeklyRestSummary(
    int Total,
    int ActiveRest,
    int CompleteRest
);

public record WeeklyActivitySummary(
    string Type,
    double? DistanceKm,
    double? DurationMinutes,
    string Intensity
);
