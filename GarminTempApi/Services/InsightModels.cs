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

public record RoutineEventSnapshot(
    int Id,
    string Title,
    string Classification,
    DateTime StartLocal,
    DateTime EndLocal,
    bool IsRace,
    bool IsRecurring,
    IReadOnlyList<int> RecurrenceDays,
    string? RecurrenceStartDate,
    string? RecurrenceEndDate,
    string? RaceName,
    string? RaceLocation,
    string? RaceGoal,
    string? Notes
);

public record RaceEventInsight(
    int Id,
    string Title,
    string? RaceName,
    DateTime StartLocal,
    DateTime EndLocal,
    string Classification,
    string Phase,
    int DaysOffset,
    string? RaceLocation,
    string? RaceGoal,
    string? Notes
);

public record CalendarInsightPayload(
    DateTime Today,
    IReadOnlyList<RoutineEventSnapshot> PastThreeDays,
    IReadOnlyList<RoutineEventSnapshot> TodayEvents,
    IReadOnlyList<RoutineEventSnapshot> TomorrowEvents,
    IReadOnlyList<RoutineEventSnapshot> NextThreeDays,
    IReadOnlyList<RaceEventInsight> RaceFocus,
    CalendarRaceConfiguration RaceConfiguration
);

public record CalendarRaceConfiguration(
    int PreparationWindowDays,
    int TaperWindowDays,
    int RecoveryWindowDays,
    int LookaheadDays
);

public record DailyRecommendationSections(
    string TodayInsight,
    string Action12h,
    string TomorrowPreparation,
    string Nutrition
);

public record DailyRecommendationHtmlSections(
    string TodayInsight,
    string Action12h,
    string TomorrowPreparation,
    string Nutrition
);
