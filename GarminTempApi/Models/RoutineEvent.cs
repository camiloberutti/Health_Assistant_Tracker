using System;

namespace GarminTempApi.Models;

public class RoutineEvent
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Classification { get; set; } = "workout cardio";
    public DateTime StartLocal { get; set; }
    public DateTime EndLocal { get; set; }
    public bool IsRace { get; set; }
    public string? RaceName { get; set; }
    public string? RaceLocation { get; set; }
    public string? RaceGoal { get; set; }
    public string? Notes { get; set; }
    public bool IsRecurring { get; set; }
    public string? RecurrenceDays { get; set; }
    public DateTime? RecurrenceStartLocal { get; set; }
    public DateTime? RecurrenceEndLocal { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
