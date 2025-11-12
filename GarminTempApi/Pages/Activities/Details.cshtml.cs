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
    private const string SwimPartial = PartialBase + "_Details.Swim.cshtml";

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
        ["meditation"] = IndoorPartial,
        ["lap_swimming"] = SwimPartial,
        ["pool_swimming"] = SwimPartial
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
    public bool IsSwimActivity { get; private set; }
    public SwimInsights? SwimData { get; private set; }
    public string SwimLapJson { get; private set; } = "[]";
    public string ActivityDisplayType { get; private set; } = string.Empty;

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

        var normalizedType = NormalizeActivityType(Activity.ActivityType);
        IsSwimActivity = string.Equals(normalizedType, "lap_swimming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "pool_swimming", StringComparison.OrdinalIgnoreCase);
        ActivityDisplayType = ToDisplayLabel(Activity.ActivityType);

        DetailPartial = ResolveDetailPartial(Activity.ActivityType);

        TrackPoints = await _db.ActivityPoints
            .AsNoTracking()
            .Where(p => p.ActivityId == Activity.ActivityDbId)
            .OrderBy(p => p.Timestamp)
            .ToListAsync();

        var visualizationPoints = BuildVisualizationPoints(TrackPoints);

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
                if (IsSwimActivity && SummaryMetrics.Count > 0)
                {
                    var duplicateLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "Calories",
                        "Distance",
                        "Elapsed",
                        "Average HR",
                        "Max HR"
                    };

                    SummaryMetrics = SummaryMetrics
                        .Where(metric => !duplicateLabels.Contains(metric.Label))
                        .ToList();
                }
                JsonBlocks = BuildJsonBlocks(detail);
                DetailErrors = detail.Errors ?? new Dictionary<string, string>();
                if (visualizationPoints.Count == 0 && detail.TrackPoints is { Count: > 0 } detailTrack)
                {
                    visualizationPoints = BuildVisualizationPoints(detailTrack);
                }
                if (IsSwimActivity)
                {
                    SwimData = ExtractSwimInsights(detail, Activity?.StartTime);
                    if (SwimData is { Laps.Count: > 0 })
                    {
                        SwimLapJson = JsonSerializer.Serialize(SwimData.Laps, VisualizationJsonOptions);
                    }
                }
            }
        }

        if (IsSwimActivity && SwimData is not null)
        {
            var swimMetrics = BuildSwimSummaryMetrics(SwimData, Activity);
            if (swimMetrics.Count > 0)
            {
                var combined = SummaryMetrics.ToList();
                combined.AddRange(swimMetrics);
                SummaryMetrics = combined;
            }
        }

        VisualizationPoints = visualizationPoints;
        TrackPointCount = VisualizationPoints.Count;
        HasGeoTrack = VisualizationPoints.Any(p => p.Latitude.HasValue && p.Longitude.HasValue);
        VisualizationJson = JsonSerializer.Serialize(VisualizationPoints, VisualizationJsonOptions);

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

        var snapshots = points
            .OrderBy(p => p.Timestamp)
            .Select(p => new TrackPointSnapshot(
                EnsureUtc(p.Timestamp),
                p.Latitude,
                p.Longitude,
                p.Altitude,
                p.HeartRate,
                null))
            .ToList();

        return BuildVisualizationPointsCore(snapshots);
    }

    private static IReadOnlyList<VisualizationPoint> BuildVisualizationPoints(IReadOnlyList<GarminTrackPoint> points)
    {
        if (points is null || points.Count == 0)
        {
            return Array.Empty<VisualizationPoint>();
        }

        var snapshots = points
            .Where(p => p.Timestamp.HasValue)
            .OrderBy(p => p.Timestamp!.Value)
            .Select(p => new TrackPointSnapshot(
                EnsureUtc(p.Timestamp!.Value),
                p.Latitude,
                p.Longitude,
                p.Altitude,
                p.HeartRate,
                p.DistanceMeters))
            .ToList();

        return BuildVisualizationPointsCore(snapshots);
    }

    private static IReadOnlyList<VisualizationPoint> BuildVisualizationPointsCore(IReadOnlyList<TrackPointSnapshot> points)
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
            var usedDistanceMeters = false;

            if (point.DistanceMeters.HasValue)
            {
                var distanceKm = Math.Max(0, point.DistanceMeters.Value / 1000d);
                cumulativeDistanceKm = Math.Max(cumulativeDistanceKm, distanceKm);
                usedDistanceMeters = true;
            }

            if (point.Latitude.HasValue && point.Longitude.HasValue)
            {
                if (!usedDistanceMeters && lastLat.HasValue && lastLon.HasValue)
                {
                    cumulativeDistanceKm += CalculateHaversineDistance(lastLat.Value, lastLon.Value, point.Latitude.Value, point.Longitude.Value);
                }

                lastLat = point.Latitude;
                lastLon = point.Longitude;
            }
            else
            {
                lastLat = null;
                lastLon = null;
            }

            var elapsedSeconds = (point.Timestamp - firstTimestamp).TotalSeconds;
            var pace = cumulativeDistanceKm > 0
                ? (elapsedSeconds / 60d) / cumulativeDistanceKm
                : (double?)null;

            var timeLabel = FormatElapsedLabel(elapsedSeconds);

            results.Add(new VisualizationPoint(
                point.Timestamp,
                point.Latitude,
                point.Longitude,
                point.HeartRate,
                point.Altitude,
                cumulativeDistanceKm,
                elapsedSeconds,
                pace,
                timeLabel));
        }

        return results;
    }

    private static string FormatElapsedLabel(double elapsedSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        return duration.TotalHours >= 1
            ? duration.ToString("hh\\:mm\\:ss")
            : duration.ToString("mm\\:ss");
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
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

        return metrics;
    }

    private static IReadOnlyList<(string Label, string Value)> BuildSwimSummaryMetrics(SwimInsights swim, ActivityDetail? activity)
    {
        var activityDistance = activity is null ? (double?)null : activity.DistanceMeters;
        var activityDuration = activity?.Duration;

        var metrics = new List<(string Label, string Value)>
        {
            ("Distance", FormatSwimDistance(swim.DistanceMeters ?? activityDistance)),
            ("Elapsed Time", FormatSwimDuration(swim.DurationSeconds, activityDuration)),
            ("Average Pace", FormatSwimPace(swim.AverageSpeedMetersPerSecond)),
            ("Average Heart Rate", FormatSwimNumber(swim.AverageHeartRate, " bpm", "0")),
            ("Max Heart Rate", FormatSwimNumber(swim.MaxHeartRate, " bpm", "0")),
            ("Calories", FormatSwimNumber(swim.Calories, " kcal", "0")),
            ("Average Cadence", FormatSwimNumber(swim.AverageSwimCadence, " spm")),
            ("Total Strokes", FormatSwimInt(swim.TotalStrokes)),
            ("Average Stroke Distance", FormatSwimNumber(swim.AverageStrokeDistance, " m", "0.00")),
            ("Average SWOLF", FormatSwimNumber(swim.AverageSwolf, suffix: string.Empty, format: "0")),
            ("Pool Length", FormatSwimPoolLength(swim.PoolLength, swim.PoolLengthUnit)),
            ("Active Lengths", FormatSwimInt(swim.ActiveLengths)),
            ("Fastest Lap", FormatSwimDuration(swim.FastestLapSeconds, null))
        };

        return metrics;
    }

    private static SwimInsights? ExtractSwimInsights(GarminActivityDetail detail, DateTime? activityStartTime)
    {
        if (string.IsNullOrWhiteSpace(detail.SummaryJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(detail.SummaryJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("summaryDTO", out var summaryDto))
        {
            return null;
        }

        DateTime? summaryStartUtc = null;
        if (summaryDto.TryGetProperty("startTimeGMT", out var startGmtElement) && startGmtElement.ValueKind == JsonValueKind.String)
        {
            summaryStartUtc = ParseOptionalDateTime(startGmtElement.GetString());
        }

        double? GetDouble(string property) => summaryDto.TryGetProperty(property, out var element) && element.TryGetDouble(out var value)
            ? value
            : null;

        int? GetInt(string property)
        {
            if (!summaryDto.TryGetProperty(property, out var element))
            {
                return null;
            }

            if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (element.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            return element.TryGetDouble(out var doubleValue)
                ? (int?)Convert.ToInt32(Math.Round(doubleValue))
                : null;
        }

        string? GetString(string property)
        {
            if (!summaryDto.TryGetProperty(property, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetDouble(out var numeric)
                    ? numeric.ToString(CultureInfo.InvariantCulture)
                    : element.GetRawText(),
                JsonValueKind.Object => element.TryGetProperty("unitKey", out var unitKey) && unitKey.ValueKind == JsonValueKind.String
                    ? unitKey.GetString()
                    : element.GetRawText(),
                _ => element.GetRawText()
            };
        }

        var startUtc = summaryStartUtc ?? ParseOptionalDateTime(summaryDto.TryGetProperty("startTimeLocal", out var localStart) && localStart.ValueKind == JsonValueKind.String ? localStart.GetString() : null);

        IReadOnlyList<SwimLap> laps = Array.Empty<SwimLap>();
        foreach (var candidate in new[] { detail.TypedSplitsJson, detail.SplitsJson, detail.DetailsJson })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            laps = ExtractSwimLaps(candidate, startUtc, activityStartTime);
            if (laps.Count > 0)
            {
                break;
            }
        }

        var distanceMeters = GetDouble("distance");
        var durationSeconds = GetDouble("duration");
        var averageSpeed = GetDouble("averageSpeed");
        var averageHeartRate = GetDouble("averageHR");
        var maxHeartRate = GetDouble("maxHR");
        var calories = GetDouble("calories");
        var averageSwimCadence = GetDouble("averageSwimCadence");
        var averageStrokeDistance = GetDouble("averageStrokeDistance");
        var averageSwolf = GetDouble("averageSWOLF");
        var poolLength = GetDouble("poolLength");
        var poolLengthUnit = GetString("unitOfPoolLength");
        var activeLengths = GetInt("numberOfActiveLengths");
        var totalStrokes = GetInt("totalNumberOfStrokes");
        var fastestLapSeconds = GetDouble("minActivityLapDuration");

        if ((!averageStrokeDistance.HasValue || averageStrokeDistance.Value <= 0.0001)
            && totalStrokes.HasValue && totalStrokes.Value > 0
            && distanceMeters.HasValue && distanceMeters.Value > 0)
        {
            averageStrokeDistance = distanceMeters.Value / totalStrokes.Value;
        }

        return new SwimInsights(
            distanceMeters,
            durationSeconds,
            averageSpeed,
            averageHeartRate,
            maxHeartRate,
            calories,
            averageSwimCadence,
            averageStrokeDistance,
            averageSwolf,
            poolLength,
            poolLengthUnit,
            activeLengths,
            totalStrokes,
            fastestLapSeconds,
            laps);
    }

    private static IReadOnlyList<SwimLap> ExtractSwimLaps(string? detailsJson, DateTime? summaryStartUtc, DateTime? activityStartTime)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return Array.Empty<SwimLap>();
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;

            JsonElement lapsElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("splits", out var splitsElement) && splitsElement.ValueKind == JsonValueKind.Object && splitsElement.TryGetProperty("lapDTOs", out var nestedLapElement) && nestedLapElement.ValueKind == JsonValueKind.Array)
            {
                lapsElement = nestedLapElement;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lapDTOs", out var directLapElement) && directLapElement.ValueKind == JsonValueKind.Array)
            {
                lapsElement = directLapElement;
            }
            else
            {
                return Array.Empty<SwimLap>();
            }

            DateTime? referenceStart = summaryStartUtc;
            if (!referenceStart.HasValue && activityStartTime.HasValue)
            {
                referenceStart = EnsureUtc(activityStartTime.Value);
            }

            if (!referenceStart.HasValue)
            {
                foreach (var lapCandidate in lapsElement.EnumerateArray())
                {
                    if (lapCandidate.TryGetProperty("startTimeGMT", out var startElement) && startElement.ValueKind == JsonValueKind.String)
                    {
                        referenceStart = ParseOptionalDateTime(startElement.GetString());
                        if (referenceStart.HasValue)
                        {
                            break;
                        }
                    }
                }
            }

            var laps = new List<SwimLap>();
            double fallbackStart = 0;

            foreach (var lapElement in lapsElement.EnumerateArray())
            {
                var lapIndex = lapElement.TryGetProperty("lapIndex", out var lapIndexElement) && lapIndexElement.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : laps.Count + 1;

                double duration = lapElement.TryGetProperty("duration", out var durationElement) && durationElement.TryGetDouble(out var parsedDouble)
                    ? parsedDouble
                    : 0d;

                double movingDuration = lapElement.TryGetProperty("movingDuration", out var movingElement) && movingElement.TryGetDouble(out parsedDouble)
                    ? parsedDouble
                    : duration;

                if (movingDuration <= 0 && lapElement.TryGetProperty("sumMovingDuration", out var sumMovingElement) && sumMovingElement.TryGetDouble(out parsedDouble))
                {
                    movingDuration = parsedDouble;
                }

                double distance = lapElement.TryGetProperty("distance", out var distanceElement) && distanceElement.TryGetDouble(out parsedDouble)
                    ? parsedDouble
                    : 0d;

                if (distance <= 0 && lapElement.TryGetProperty("sumDistance", out var sumDistanceElement) && sumDistanceElement.TryGetDouble(out parsedDouble))
                {
                    distance = parsedDouble;
                }

                var lapStartUtc = lapElement.TryGetProperty("startTimeGMT", out var startElement) && startElement.ValueKind == JsonValueKind.String
                    ? ParseOptionalDateTime(startElement.GetString())
                    : null;

                double startOffsetSeconds;
                if (lapStartUtc.HasValue && referenceStart.HasValue)
                {
                    startOffsetSeconds = Math.Max(0, (lapStartUtc.Value - referenceStart.Value).TotalSeconds);
                    fallbackStart = startOffsetSeconds;
                }
                else
                {
                    startOffsetSeconds = fallbackStart;
                }

                double restDuration = Math.Max(0, duration - movingDuration);
                bool isRestLap = (distance <= 0.05 && movingDuration <= 0.1) || movingDuration <= 0.1;

                double? paceSecondsPer100 = null;
                var movingForPace = movingDuration > 0 ? movingDuration : duration;
                if (!isRestLap && distance > 0.01 && movingForPace > 0.01)
                {
                    paceSecondsPer100 = movingForPace / (distance / 100d);
                }

                double? avgHr = lapElement.TryGetProperty("averageHR", out var avgHrElement) && avgHrElement.TryGetDouble(out parsedDouble)
                    ? parsedDouble
                    : null;

                double? maxHr = lapElement.TryGetProperty("maxHR", out var maxHrElement) && maxHrElement.TryGetDouble(out parsedDouble)
                    ? parsedDouble
                    : null;

                laps.Add(new SwimLap(
                    lapIndex,
                    startOffsetSeconds,
                    duration,
                    movingDuration,
                    restDuration,
                    distance,
                    isRestLap,
                    paceSecondsPer100,
                    avgHr,
                    maxHr));

                fallbackStart = startOffsetSeconds + duration;
            }

            return laps;
        }
        catch (JsonException)
        {
            return Array.Empty<SwimLap>();
        }
    }

    private static string FormatSwimDistance(double? meters)
    {
        if (!meters.HasValue)
        {
            return "—";
        }

        return meters.Value >= 1000
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.00} km", meters.Value / 1000d)
            : string.Format(CultureInfo.CurrentCulture, "{0:0} m", meters.Value);
    }

    private static string FormatSwimDuration(double? seconds, TimeSpan? fallback)
    {
        if (seconds.HasValue)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds.Value));
            return span.TotalHours >= 1
                ? span.ToString("hh\\:mm\\:ss")
                : span.ToString("mm\\:ss");
        }

        if (fallback.HasValue)
        {
            var span = fallback.Value;
            return span.TotalHours >= 1
                ? span.ToString("hh\\:mm\\:ss")
                : span.ToString("mm\\:ss");
        }

        return "—";
    }

    private static string FormatSwimPace(double? averageSpeedMetersPerSecond)
    {
        if (!averageSpeedMetersPerSecond.HasValue || averageSpeedMetersPerSecond.Value <= 0)
        {
            return "—";
        }

        var seconds = 100d / averageSpeedMetersPerSecond.Value;
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.ToString("m\\:ss") + " /100m";
    }

    private static string FormatSwimNumber(double? value, string suffix = "", string format = "0.#")
    {
        return value.HasValue
            ? string.Format(CultureInfo.CurrentCulture, "{0:" + format + "}{1}", value.Value, suffix)
            : "—";
    }

    private static string FormatSwimInt(int? value)
    {
        return value.HasValue
            ? value.Value.ToString("N0", CultureInfo.CurrentCulture)
            : "—";
    }

    private static string FormatSwimPoolLength(double? length, string? unit)
    {
        if (!length.HasValue)
        {
            return "—";
        }

        var formattedLength = length.Value.ToString("0.#", CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(unit)
            ? formattedLength + " m"
            : formattedLength + " " + unit;
    }

    private static DateTime? ParseOptionalDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            if (parsed.Kind == DateTimeKind.Unspecified)
            {
                parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }

        return null;
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

    public sealed record SwimInsights(
        double? DistanceMeters,
        double? DurationSeconds,
        double? AverageSpeedMetersPerSecond,
        double? AverageHeartRate,
        double? MaxHeartRate,
        double? Calories,
        double? AverageSwimCadence,
        double? AverageStrokeDistance,
        double? AverageSwolf,
        double? PoolLength,
        string? PoolLengthUnit,
        int? ActiveLengths,
        int? TotalStrokes,
        double? FastestLapSeconds,
        IReadOnlyList<SwimLap> Laps);

    public sealed record SwimLap(
        int LapIndex,
        double StartOffsetSeconds,
        double DurationSeconds,
        double MovingDurationSeconds,
        double RestDurationSeconds,
        double DistanceMeters,
        bool IsRest,
        double? PaceSecondsPer100Meters,
        double? AverageHeartRate,
        double? MaxHeartRate);

    private sealed record TrackPointSnapshot(
        DateTime Timestamp,
        double? Latitude,
        double? Longitude,
        double? Altitude,
        double? HeartRate,
        double? DistanceMeters);

    private static string ToDisplayLabel(string? rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
        {
            return "Activity";
        }

        var cleaned = rawType
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Trim();

        if (cleaned.Length == 0)
        {
            return "Activity";
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned);
    }
}
