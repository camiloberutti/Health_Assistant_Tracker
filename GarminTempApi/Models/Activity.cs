using System;
using System.Collections.Generic;

namespace GarminTempApi.Models
{
    public class Activity
    {
        public int Id { get; set; }
        public string ExternalId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;         // Source label such as "upload" or "garminconnect"
        public string FileName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public double DistanceMeters { get; set; }
        public TimeSpan Duration { get; set; }
        public string ActivityType { get; set; } = string.Empty;   // Run, Ride, etc.
        public List<ActivityPoint> Points { get; set; } = new();
        public ActivityDetailSnapshot? DetailSnapshot { get; set; }
    }

    public class ActivityPoint
    {
        public int Id { get; set; }
        public int ActivityId { get; set; }
        public Activity Activity { get; set; } = null!;
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? HeartRate { get; set; }
        public double? Altitude { get; set; }
    }
}
