using System;

namespace GarminTempApi.Models;

public class SleepDetailSnapshot
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string DetailJson { get; set; } = string.Empty;
    public DateTime LastUpdatedUtc { get; set; }
}
