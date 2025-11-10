using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Pages.Sleep;

public class TodayModel : PageModel
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions DetailJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public TodayModel(AppDbContext db) => _db = db;

    public SleepEntry? Entry { get; private set; }
    public IReadOnlyList<SleepStageSlice> StageSlices { get; private set; } = Array.Empty<SleepStageSlice>();
    public IReadOnlyList<SleepTimelineSegment> StageTimeline { get; private set; } = Array.Empty<SleepTimelineSegment>();
    public IReadOnlyList<TimelineSample> MovementSamples { get; private set; } = Array.Empty<TimelineSample>();
    public IReadOnlyList<TimelineSample> RestingHeartRateSamples { get; private set; } = Array.Empty<TimelineSample>();
    public IReadOnlyList<TimelineSample> BodyBatterySamples { get; private set; } = Array.Empty<TimelineSample>();
    public DateTime? SelectedDate { get; private set; }
    public DateTime? PreviousDate { get; private set; }
    public DateTime? NextDate { get; private set; }
    public double? GoalDeltaSeconds { get; private set; }
    public string? QualityLabel { get; private set; }

    public bool HasData => Entry != null;

    public async Task OnGetAsync(DateTime? date)
    {
        var rows = await _db.SleepSummaries
            .OrderBy(s => s.Date)
            .Select(s => new SleepEntry
            {
                Date = s.Date,
                TotalSleepSeconds = s.TotalSleepSeconds,
                DeepSleepSeconds = s.DeepSleepSeconds,
                LightSleepSeconds = s.LightSleepSeconds,
                RemSleepSeconds = s.RemSleepSeconds,
                AwakeSeconds = s.AwakeSeconds,
                SleepScore = s.SleepScore,
                SleepQualityType = s.SleepQualityType,
                SleepStartLocal = s.SleepStartLocal,
                SleepEndLocal = s.SleepEndLocal,
                SleepStartGmt = s.SleepStartGmt,
                SleepEndGmt = s.SleepEndGmt,
                RestingHeartRate = s.RestingHeartRate,
                BodyBatteryChange = s.BodyBatteryChange,
                AverageRespirationValue = s.AverageRespirationValue,
                LowestSpO2Value = s.LowestSpO2Value,
                SleepGoalSeconds = s.SleepGoalSeconds
            })
            .ToListAsync();

        if (rows.Count == 0)
        {
            return;
        }

        SleepEntry? selectedEntry = null;
        DateTime? selectedDate = date?.Date;

        if (selectedDate.HasValue)
        {
            selectedEntry = rows.LastOrDefault(row => row.Date.Date == selectedDate.Value);
        }

        if (selectedEntry == null)
        {
            selectedEntry = rows[^1];
            selectedDate = selectedEntry.Date.Date;
        }

        Entry = selectedEntry;
        SelectedDate = selectedDate;

        if (SelectedDate.HasValue)
        {
            var index = rows.FindIndex(row => row.Date.Date == SelectedDate.Value);
            if (index >= 0)
            {
                PreviousDate = index > 0 ? rows[index - 1].Date.Date : null;
                NextDate = index < rows.Count - 1 ? rows[index + 1].Date.Date : null;
            }
        }

        if (Entry == null)
        {
            return;
        }

        StageSlices = new List<SleepStageSlice>
        {
            new("Deep", Entry.DeepSleepSeconds, "#1e3a8a"),
            new("Light", Entry.LightSleepSeconds, "#60a5fa"),
            new("REM", Entry.RemSleepSeconds, "#d946ef"),
            new("Awake", Entry.AwakeSeconds, "#facc15")
        };

        StageTimeline = BuildStageTimelineFromSummary(selectedEntry);
        MovementSamples = Array.Empty<TimelineSample>();
        RestingHeartRateSamples = Array.Empty<TimelineSample>();
        BodyBatterySamples = Array.Empty<TimelineSample>();

        if (SelectedDate.HasValue)
        {
            var detailSnapshot = await _db.SleepDetailSnapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Date == SelectedDate.Value);

            if (detailSnapshot is not null && !string.IsNullOrWhiteSpace(detailSnapshot.DetailJson))
            {
                try
                {
                    var detail = JsonSerializer.Deserialize<SleepDetailDocument>(detailSnapshot.DetailJson, DetailJsonOptions);
                    if (detail is not null)
                    {
                        var detailTimeline = BuildStageTimelineFromDetail(detail);
                        if (detailTimeline.Count > 0)
                        {
                            StageTimeline = detailTimeline;
                        }

                        MovementSamples = BuildSamples(detail.Movement);
                        RestingHeartRateSamples = BuildSamples(detail.HeartRate);
                        BodyBatterySamples = BuildSamples(detail.BodyBattery);
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed sleep detail payloads and fall back to summary data.
                }
            }
        }

        GoalDeltaSeconds = Entry.SleepGoalSeconds.HasValue
            ? Entry.TotalSleepSeconds - Entry.SleepGoalSeconds.Value
            : null;

        QualityLabel = string.IsNullOrWhiteSpace(Entry.SleepQualityType)
            ? null
            : Entry.SleepQualityType.Trim();
    }

    public class SleepEntry
    {
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

    public record SleepStageSlice(string Label, double Seconds, string Color);
    public record SleepTimelineSegment(DateTime Start, DateTime End, string Stage, string Color);
    public record TimelineSample(DateTime Timestamp, double Value);

    private static IReadOnlyList<SleepTimelineSegment> BuildStageTimelineFromSummary(SleepEntry entry)
    {
        var start = entry.SleepStartLocal ?? entry.SleepStartGmt;
        var end = entry.SleepEndLocal ?? entry.SleepEndGmt;
        if (!start.HasValue || !end.HasValue || start >= end)
        {
            return Array.Empty<SleepTimelineSegment>();
        }

        var segments = new List<SleepTimelineSegment>();
        var span = end.Value - start.Value;
        if (span.TotalSeconds <= 0)
        {
            return segments;
        }

        foreach (var tuple in new (string Stage, double Seconds, string Color)[]
                 {
                     ("Deep", entry.DeepSleepSeconds, "#1e3a8a"),
                     ("Light", entry.LightSleepSeconds, "#60a5fa"),
                     ("REM", entry.RemSleepSeconds, "#d946ef"),
                     ("Awake", entry.AwakeSeconds, "#facc15")
                 })
        {
            if (tuple.Seconds <= 0)
            {
                continue;
            }

            var segmentStart = segments.Count == 0
                ? start.Value
                : segments[^1].End;
            var segmentEnd = segmentStart.AddSeconds(tuple.Seconds);

            if (segmentEnd > end.Value)
            {
                segmentEnd = end.Value;
            }

            segments.Add(new SleepTimelineSegment(segmentStart, segmentEnd, tuple.Stage, tuple.Color));

            if (segmentEnd >= end.Value)
            {
                break;
            }
        }

        if (segments.Count == 0)
        {
            segments.Add(new SleepTimelineSegment(start.Value, end.Value, "Light", "#60a5fa"));
        }

        return segments;
    }

    private static IReadOnlyList<SleepTimelineSegment> BuildStageTimelineFromDetail(SleepDetailDocument detail)
    {
        if (detail.Levels is null || detail.Levels.Count == 0)
        {
            return Array.Empty<SleepTimelineSegment>();
        }

        var segments = new List<SleepTimelineSegment>();
        foreach (var level in detail.Levels.OrderBy(l => ParseIsoTimestamp(l.StartUtc)))
        {
            if (string.IsNullOrWhiteSpace(level.Stage))
            {
                continue;
            }

            var start = ParseIsoTimestamp(level.StartUtc);
            var end = ParseIsoTimestamp(level.EndUtc);

            if (start is null || end is null || end <= start)
            {
                continue;
            }

            segments.Add(new SleepTimelineSegment(start.Value, end.Value, level.Stage, GetStageColor(level.Stage)));
        }

        return segments;
    }

    private static IReadOnlyList<TimelineSample> BuildSamples(IEnumerable<ValueSampleDocument>? samples)
    {
        if (samples is null)
        {
            return Array.Empty<TimelineSample>();
        }

        var list = new List<TimelineSample>();
        foreach (var sample in samples)
        {
            if (sample is null)
            {
                continue;
            }

            var timestamp = ParseIsoTimestamp(sample.TimestampUtc);
            if (timestamp is null || sample.Value is null)
            {
                continue;
            }

            list.Add(new TimelineSample(timestamp.Value, sample.Value.Value));
        }

        return list
            .OrderBy(s => s.Timestamp)
            .ToList();
    }

    private static DateTime? ParseIsoTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string GetStageColor(string stage) => stage switch
    {
        "Deep" => "#1e3a8a",
        "Light" => "#60a5fa",
        "REM" => "#d946ef",
        "Awake" => "#facc15",
        _ => "#60a5fa"
    };

    private sealed class SleepDetailDocument
    {
        public List<SleepLevelDocument> Levels { get; set; } = new();
        public List<ValueSampleDocument> Movement { get; set; } = new();
        public List<ValueSampleDocument> HeartRate { get; set; } = new();
        public List<ValueSampleDocument> BodyBattery { get; set; } = new();
    }

    private sealed class SleepLevelDocument
    {
        public string Stage { get; set; } = string.Empty;
        public string? StartUtc { get; set; }
        public string? EndUtc { get; set; }
    }

    private sealed class ValueSampleDocument
    {
        public string? TimestampUtc { get; set; }
        public double? Value { get; set; }
    }
}
