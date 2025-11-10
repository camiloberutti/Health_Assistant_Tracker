using System;

namespace GarminTempApi.Models;

public class SleepSummary
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public double TotalSleepSeconds { get; set; }
    public double DeepSleepSeconds { get; set; }
    public double LightSleepSeconds { get; set; }
    public double RemSleepSeconds { get; set; }
    public double AwakeSeconds { get; set; }
    public double? SleepScore { get; set; }
    public string SleepQualityType { get; set; } = string.Empty;
    public DateTime? SleepStartLocal { get; set; }
    public DateTime? SleepEndLocal { get; set; }
    public DateTime? SleepStartGmt { get; set; }
    public DateTime? SleepEndGmt { get; set; }
    public double? RestingHeartRate { get; set; }
    public double? BodyBatteryChange { get; set; }
    public double? AverageRespirationValue { get; set; }
    public double? LowestSpO2Value { get; set; }
    public double? SleepGoalSeconds { get; set; }
}
