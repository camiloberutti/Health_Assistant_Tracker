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
