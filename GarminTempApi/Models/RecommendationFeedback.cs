using System;

namespace GarminTempApi.Models;

public class RecommendationFeedback
{
    public int Id { get; set; }
    public DateTime SubmittedUtc { get; set; }
    public bool Helpful { get; set; }
    public string? FocusArea { get; set; }
    public string? Notes { get; set; }
}
