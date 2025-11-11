using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using GarminTempApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Pages.Activities;

public class DetailsModel : PageModel
{
    private const string PartialBase = "/Pages/Activities/DetailsPartials/";
    private const string MapPartial = PartialBase + "_Details.MapAndCharts.cshtml";
    private const string IndoorPartial = PartialBase + "_Details.Indoor.cshtml";

    private static readonly Dictionary<string, string> ActivityTemplateOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["strength_training"] = IndoorPartial,
        ["hiit"] = IndoorPartial,
        ["yoga"] = IndoorPartial,
        ["pilates"] = IndoorPartial,
        ["indoor_cycling"] = IndoorPartial,
        ["indoor_running"] = IndoorPartial,
        ["treadmill_running"] = IndoorPartial,
        ["elliptical"] = IndoorPartial,
        ["meditation"] = IndoorPartial
    };

    private static readonly JsonSerializerOptions VisualizationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDbContext _db;

    public DetailsModel(AppDbContext db) => _db = db;

    public ActivityDetail? Activity { get; private set; }
    public IReadOnlyList<ActivityPoint> TrackPoints { get; private set; } = Array.Empty<ActivityPoint>();
    public int TrackPointCount { get; private set; }
    public bool HasGeoTrack { get; private set; }
    public GarminActivityDetail? Detail { get; private set; }
    public IReadOnlyList<(string Label, string Value)> SummaryMetrics { get; private set; } = Array.Empty<(string, string)>();
    public IReadOnlyList<DetailJsonBlock> JsonBlocks { get; private set; } = Array.Empty<DetailJsonBlock>();
    public IReadOnlyDictionary<string, string> DetailErrors { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyList<VisualizationPoint> VisualizationPoints { get; private set; } = Array.Empty<VisualizationPoint>();
    public string VisualizationJson { get; private set; } = "[]";
    public string DetailPartial { get; private set; } = MapPartial;

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        Activity = await _db.Activities
                .AsNoTracking()
                .Where(a => a.ExternalId == id)
                .Select(a => new ActivityDetail
                {
                    ActivityDbId = a.Id,
                    ExternalId = a.ExternalId,
                    Name = string.IsNullOrWhiteSpace(a.FileName) ? a.ExternalId : a.FileName,
                    StartTime = a.StartTime,
                    DistanceMeters = a.DistanceMeters,
                    Duration = a.Duration,
                    ActivityType = a.ActivityType,
                    Source = a.Source
                })
                .FirstOrDefaultAsync();

        if (Activity is null)
        {
            return NotFound();
        }

        DetailPartial = ResolveDetailPartial(Activity.ActivityType);

        TrackPoints = await _db.ActivityPoints
            .AsNoTracking()
            .Where(p => p.ActivityId == Activity.ActivityDbId)
            .OrderBy(p => p.Timestamp)
            .ToListAsync();

        TrackPointCount = TrackPoints.Count;
        VisualizationPoints = BuildVisualizationPoints(TrackPoints);
        HasGeoTrack = VisualizationPoints.Any(p => p.Latitude.HasValue && p.Longitude.HasValue);
        VisualizationJson = JsonSerializer.Serialize(VisualizationPoints, VisualizationJsonOptions);

        var detailJson = await _db.ActivityDetailSnapshots
            .AsNoTracking()
            .Where(d => d.ActivityId == Activity.ActivityDbId)
            .Select(d => d.DetailJson)
            .FirstOrDefaultAsync(cancellationToken: default);

        if (!string.IsNullOrWhiteSpace(detailJson))
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Detail = JsonSerializer.Deserialize<GarminActivityDetail>(detailJson, options);
            if (Detail is { } detail)
            {
                SummaryMetrics = BuildSummaryMetrics(detail);
                JsonBlocks = BuildJsonBlocks(detail);
                DetailErrors = detail.Errors ?? new Dictionary<string, string>();
            }
        }

        return Page();
    }

    private static string ResolveDetailPartial(string? activityType)
    {
        var normalized = NormalizeActivityType(activityType);

        if (normalized.Length == 0)
        {
            return MapPartial;
        }

        if (ActivityTemplateOverrides.TryGetValue(normalized, out var template))
        {
            return template;
        }

        return MapPartial;
    }

    private static string NormalizeActivityType(string? activityType)
    {
        if (string.IsNullOrWhiteSpace(activityType))
        {
            return string.Empty;
        }

        return activityType
            .Trim()
            .Replace(' ', '_')
            .Replace('-', '_')
            .ToLowerInvariant();
    }

    private static IReadOnlyList<VisualizationPoint> BuildVisualizationPoints(IReadOnlyList<ActivityPoint> points)
    {
        if (points.Count == 0)
        {
            return Array.Empty<VisualizationPoint>();
        }

        var firstTimestamp = points[0].Timestamp;
        var results = new List<VisualizationPoint>(points.Count);

        double cumulativeDistanceKm = 0;
        double? lastLat = null;
        double? lastLon = null;

        foreach (var point in points)
        {
            if (lastLat.HasValue && lastLon.HasValue)
            {
                cumulativeDistanceKm += CalculateHaversineDistance(lastLat.Value, lastLon.Value, point.Latitude, point.Longitude);
            }

            lastLat = point.Latitude;
            lastLon = point.Longitude;

            var elapsedSeconds = (point.Timestamp - firstTimestamp).TotalSeconds;
            var pace = cumulativeDistanceKm > 0
                ? (elapsedSeconds / 60d) / cumulativeDistanceKm
                : (double?)null;

            var localTimestamp = point.Timestamp.Kind switch
            {
                DateTimeKind.Utc => point.Timestamp.ToLocalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(point.Timestamp, DateTimeKind.Local),
                _ => point.Timestamp
            };

            results.Add(new VisualizationPoint(
                point.Timestamp,
                point.Latitude,
                point.Longitude,
                point.HeartRate,
                point.Altitude,
                cumulativeDistanceKm,
                elapsedSeconds,
                pace,
                localTimestamp.ToString("T", CultureInfo.CurrentCulture)));
        }

        return results;
    }

    private static double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusKm = 6371d;

        double ToRadians(double angle) => Math.PI * angle / 180d;

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Pow(Math.Sin(dLon / 2), 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusKm * c;
    }

    private static IReadOnlyList<(string Label, string Value)> BuildSummaryMetrics(GarminActivityDetail detail)
    {
        if (string.IsNullOrWhiteSpace(detail.SummaryJson))
        {
            return Array.Empty<(string, string)>();
        }

        var metrics = new List<(string, string)>();

        using var doc = JsonDocument.Parse(detail.SummaryJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("summaryDTO", out var summaryDto))
        {
            AddMetric(metrics, "Calories", summaryDto, "calories", element => FormatNumber(element, suffix: " kcal"));
            AddMetric(metrics, "Distance", summaryDto, "distance", element =>
            {
                return element.TryGetDouble(out var meters)
                    ? $"{meters / 1000d:0.00} km"
                    : element.GetRawText();
            });
            AddMetric(metrics, "Elapsed", summaryDto, "duration", element =>
            {
                return element.TryGetDouble(out var seconds)
                    ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss")
                    : element.GetRawText();
            });
            AddMetric(metrics, "Average HR", summaryDto, "averageHR", element => FormatNumber(element, suffix: " bpm"));
            AddMetric(metrics, "Max HR", summaryDto, "maxHR", element => FormatNumber(element, suffix: " bpm"));
            AddMetric(metrics, "Average Speed", summaryDto, "averageSpeed", element =>
            {
                return element.TryGetDouble(out var metersPerSecond)
                    ? $"{metersPerSecond * 3.6:0.00} km/h"
                    : element.GetRawText();
            });
            AddMetric(metrics, "Elevation Gain", summaryDto, "elevationGain", element => FormatNumber(element, suffix: " m"));
        }

        if (root.TryGetProperty("activityTypeDTO", out var typeDto) && typeDto.TryGetProperty("typeKey", out var typeKey))
        {
            var typeLabel = typeKey.GetString();
            if (!string.IsNullOrWhiteSpace(typeLabel))
            {
                metrics.Add(("Type", typeLabel));
            }
        }

        return metrics;
    }

    private static void AddMetric(List<(string Label, string Value)> metrics, string label, JsonElement parent, string propertyName, Func<JsonElement, string>? formatter = null)
    {
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        string value;
        if (formatter is not null)
        {
            value = formatter(element);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
        }
        else if (element.TryGetDouble(out var number))
        {
            value = number.ToString("0.##");
        }
        else
        {
            value = element.GetRawText();
        }

        metrics.Add((label, value));
    }

    private static string FormatNumber(JsonElement element, string suffix)
    {
        return element.TryGetDouble(out var number)
            ? $"{number:0.#}{suffix}"
            : element.GetRawText();
    }

    private static IReadOnlyList<DetailJsonBlock> BuildJsonBlocks(GarminActivityDetail detail)
    {
        var blocks = new List<DetailJsonBlock>();
        AppendBlock(blocks, "Summary", detail.SummaryJson);
        AppendBlock(blocks, "Details", detail.DetailsJson);
        AppendBlock(blocks, "Split Summaries", detail.SplitSummariesJson);
        AppendBlock(blocks, "Splits", detail.SplitsJson);
        AppendBlock(blocks, "Typed Splits", detail.TypedSplitsJson);
        AppendBlock(blocks, "Weather", detail.WeatherJson);
        AppendBlock(blocks, "Heart Rate Zones", detail.HeartRateZonesJson);
        AppendBlock(blocks, "Gear", detail.GearJson);
        AppendBlock(blocks, "Exercise Sets", detail.ExerciseSetsJson);
        return blocks;
    }

    private static void AppendBlock(List<DetailJsonBlock> blocks, string title, string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return;
        }

        if (TryFormatJson(rawJson, out var formatted))
        {
            blocks.Add(new DetailJsonBlock(title, formatted));
        }
    }

    private static bool TryFormatJson(string raw, out string formatted)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            formatted = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (JsonException)
        {
            formatted = raw;
            return false;
        }
    }

    public sealed record ActivityDetail
    {
        public int ActivityDbId { get; init; }
        public string ExternalId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public DateTime StartTime { get; init; }
        public double DistanceMeters { get; init; }
        public TimeSpan Duration { get; init; }
        public string ActivityType { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
    }

    public sealed record VisualizationPoint(
        DateTime Timestamp,
        double? Latitude,
        double? Longitude,
        double? HeartRate,
        double? Altitude,
        double DistanceKm,
        double ElapsedSeconds,
        double? PaceMinutesPerKm,
        string TimeLabel);

    public sealed record DetailJsonBlock(string Title, string Json);
}
