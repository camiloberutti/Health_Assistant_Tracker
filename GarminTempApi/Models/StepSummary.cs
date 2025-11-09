using System;

namespace GarminTempApi.Models;

public class StepSummary
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public double TotalSteps { get; set; }
    public double GoalSteps { get; set; }
    public double TotalCalories { get; set; }
    public double ActiveCalories { get; set; }
    public double TotalDistanceMeters { get; set; }
}
