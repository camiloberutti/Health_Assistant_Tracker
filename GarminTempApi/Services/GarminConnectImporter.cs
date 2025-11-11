using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

public class GarminConnectImporter
{
    private readonly IConfiguration _config;
    private readonly ILogger<GarminConnectImporter> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GarminConnectImporter(IConfiguration config, ILogger<GarminConnectImporter> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool HasCredentials()
    {
        var (username, password) = GetCredentials();
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }

    public TimeSpan? GetRefreshInterval()
    {
        var minutesValue = GetConfigValue("RefreshMinutes") ?? Environment.GetEnvironmentVariable("GARMIN_REFRESH_MINUTES");
        if (double.TryParse(minutesValue, out var minutes) && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }
        return null;
    }

    public async Task<GarminConnectFetchResult> FetchActivitiesAsync(CancellationToken cancellationToken = default)
    {
        var (username, password) = GetCredentials();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Garmin credentials are not configured.");
        }

        var scriptPath = ResolveScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Garmin fetch script not found at {scriptPath}.");
        }

        var pythonExecutable = _config["GarminConnect:PythonExecutable"];
        if (string.IsNullOrWhiteSpace(pythonExecutable))
        {
            pythonExecutable = OperatingSystem.IsWindows() ? "python" : "python3";
        }

        var startDate = GetConfigValue("StartDate") ?? Environment.GetEnvironmentVariable("GARMIN_START_DATE");
        var endDate = GetConfigValue("EndDate") ?? Environment.GetEnvironmentVariable("GARMIN_END_DATE");
        var maxCountValue = GetConfigValue("MaxCount") ?? Environment.GetEnvironmentVariable("GARMIN_MAX_ACTIVITIES");
        var stepsOnly = ShouldFetchStepsOnly();

        var psi = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--username");
        psi.ArgumentList.Add(username);
        psi.ArgumentList.Add("--password");
        psi.ArgumentList.Add(password);

        if (!string.IsNullOrWhiteSpace(startDate))
        {
            psi.ArgumentList.Add("--start-date");
            psi.ArgumentList.Add(startDate);
        }

        if (!string.IsNullOrWhiteSpace(endDate))
        {
            psi.ArgumentList.Add("--end-date");
            psi.ArgumentList.Add(endDate);
        }

        if (!string.IsNullOrWhiteSpace(maxCountValue))
        {
            psi.ArgumentList.Add("--max-count");
            psi.ArgumentList.Add(maxCountValue);
        }

        if (stepsOnly)
        {
            psi.ArgumentList.Add("--steps-only");
            _logger.LogInformation("Garmin fetch configured for steps-only mode.");
        }

        _logger.LogInformation("Running Garmin Connect fetch script {ScriptPath}...", scriptPath);
        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start Garmin fetch process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Garmin fetch script failed with exit code {process.ExitCode}.\nSTDOUT: {stdout}\nSTDERR: {stderr}");
        }

        GarminFetchPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<GarminFetchPayload>(stdout, JsonOptions) ?? new GarminFetchPayload();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Garmin fetch output: {Message}", ex.Message);
            throw new InvalidOperationException("Failed to parse Garmin fetch output.", ex);
        }

        var activities = (payload.Activities ?? new List<GarminConnectActivityDto>())
            .Select(dto => dto.ToActivity())
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        var sleep = (payload.Sleep ?? new List<GarminSleepDto>())
            .Select(dto => dto.ToSleepEntry())
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        var sleepDetails = (payload.SleepDetails ?? new List<GarminSleepDetailDto>())
            .Select(dto => dto.ToDetail())
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        var steps = (payload.Steps ?? new List<GarminStepsDto>())
            .Select(dto => dto.ToStepsEntry())
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        var (windowStart, windowEnd) = payload.Window?.ToRange() ?? (null, null);

        return new GarminConnectFetchResult(activities, sleep, sleepDetails, steps, windowStart, windowEnd, stdout, stderr);
    }

    private (string? Username, string? Password) GetCredentials()
    {
        var username = GetConfigValue("Username") ?? Environment.GetEnvironmentVariable("GARMIN_USERNAME");
        var password = GetConfigValue("Password") ?? Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        return (username, password);
    }

    private string? GetConfigValue(string key) => _config[$"GarminConnect:{key}"];

    private bool ShouldFetchStepsOnly()
    {
        var value = GetConfigValue("StepsOnly") ?? Environment.GetEnvironmentVariable("GARMIN_FETCH_STEPS_ONLY");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveScriptPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var directPath = Path.Combine(baseDirectory, "scripts", "garmin_fetch.py");
        if (File.Exists(directPath))
        {
            return directPath;
        }

        // Fallback: script may reside alongside the executable.
        var fallback = Path.Combine(baseDirectory, "garmin_fetch.py");
        return fallback;
    }

    private static string? SerializeJsonElement(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null
            ? null
            : element.GetRawText();
    }

    private static DateTime? ParseDateTime(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return DateTime.TryParse(input, out var parsed) ? parsed : null;
    }

    private static List<GarminTrackPoint> BuildTrackPoints(GarminActivityDetailDto detail)
    {
        var basePoints = detail.TrackPoints is not null
            ? detail.TrackPoints
                .Select(tp => tp.ToTrackPoint())
                .Where(tp => tp is not null)
                .Select(tp => tp!)
                .ToList()
            : new List<GarminTrackPoint>();

        var hasCoordinates = basePoints.Any(p => p.Latitude.HasValue && p.Longitude.HasValue);
        if (hasCoordinates)
        {
            return basePoints;
        }

        var fallback = BuildTrackPointsFromPolyline(detail.Details);

        if (fallback.Count > 0 && basePoints.Count > 0)
        {
            var pointsWithHeartRate = basePoints
                .Where(p => p.Timestamp.HasValue && p.HeartRate.HasValue)
                .ToList();

            if (pointsWithHeartRate.Count > 0)
            {
                for (var i = 0; i < fallback.Count; i++)
                {
                    var candidate = fallback[i];
                    if (candidate.Timestamp is null)
                    {
                        continue;
                    }

                    var nearest = pointsWithHeartRate
                        .Select(p => new { Point = p, Delta = Math.Abs((p.Timestamp!.Value - candidate.Timestamp.Value).TotalSeconds) })
                        .OrderBy(x => x.Delta)
                        .FirstOrDefault();

                    if (nearest is not null && nearest.Delta <= 2 && nearest.Point.HeartRate.HasValue)
                    {
                        fallback[i] = candidate with { HeartRate = nearest.Point.HeartRate };
                    }
                }
            }
        }

        if (fallback.Count > 0)
        {
            return fallback;
        }

        return basePoints;
    }

    private static List<GarminTrackPoint> BuildTrackPointsFromPolyline(JsonElement detailsElement)
    {
        var result = new List<GarminTrackPoint>();

        void TryPopulate(JsonElement candidate)
        {
            if (result.Count > 0)
            {
                return;
            }

            if (candidate.ValueKind == JsonValueKind.Object)
            {
                TryPopulateFromObject(candidate, result);
            }
            else if (candidate.ValueKind == JsonValueKind.String)
            {
                var raw = candidate.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    TryPopulateFromObject(doc.RootElement, result);
                }
                catch (JsonException)
                {
                    // Ignore malformed fallback JSON.
                }
            }
        }

        TryPopulate(detailsElement);

        return result;
    }

    private static void TryPopulateFromObject(JsonElement root, List<GarminTrackPoint> target)
    {
        if (target.Count > 0)
        {
            return;
        }

        if (root.TryGetProperty("geoPolylineDTO", out var geoPolyline) && geoPolyline.ValueKind == JsonValueKind.Object)
        {
            TryPopulateFromGeoPolyline(geoPolyline, target);
        }

        if (target.Count == 0 && root.TryGetProperty("detailsJson", out var nestedJson) && nestedJson.ValueKind == JsonValueKind.String)
        {
            var raw = nestedJson.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    TryPopulateFromObject(doc.RootElement, target);
                }
            }
            catch (JsonException)
            {
                // malformed JSON, ignore.
            }
        }
    }

    private static void TryPopulateFromGeoPolyline(JsonElement geoPolyline, List<GarminTrackPoint> target)
    {
        if (target.Count > 0)
        {
            return;
        }

        if (!TryResolvePolylineArray(geoPolyline, out var coordinates))
        {
            return;
        }

        foreach (var coordinate in coordinates.EnumerateArray())
        {
            if (coordinate.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryReadDouble(coordinate, "lat", out var lat) || !TryReadDouble(coordinate, "lon", out var lon))
            {
                continue;
            }

            if (coordinate.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            double? altitude = null;
            if (TryReadDouble(coordinate, "altitude", out var altitudeValue))
            {
                altitude = altitudeValue;
            }

            var timestamp = ParsePolylineTime(coordinate);

            target.Add(new GarminTrackPoint(timestamp, lat, lon, altitude, null));
        }
    }

    private static bool TryResolvePolylineArray(JsonElement geoPolyline, out JsonElement coordinates)
    {
        coordinates = default;

        if (!geoPolyline.TryGetProperty("polyline", out var polyline))
        {
            return false;
        }

        if (polyline.ValueKind == JsonValueKind.Array)
        {
            coordinates = polyline;
            return true;
        }

        if (polyline.ValueKind == JsonValueKind.Object)
        {
            if (polyline.TryGetProperty("coordinates", out var coords) && coords.ValueKind == JsonValueKind.Array)
            {
                coordinates = coords;
                return true;
            }

            if (polyline.TryGetProperty("coordinateList", out var coordList) && coordList.ValueKind == JsonValueKind.Array)
            {
                coordinates = coordList;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out value);
        }

        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static DateTime? ParsePolylineTime(JsonElement coordinate)
    {
        if (coordinate.TryGetProperty("time", out var timeElement))
        {
            if (timeElement.ValueKind == JsonValueKind.Number && timeElement.TryGetInt64(out var millis))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            if (timeElement.ValueKind == JsonValueKind.String)
            {
                return ParseDateTime(timeElement.GetString());
            }
        }

        return null;
    }

    private sealed record GarminFetchPayload
    {
        public GarminWindowDto? Window { get; init; }
        public List<GarminConnectActivityDto>? Activities { get; init; }
        public List<GarminSleepDto>? Sleep { get; init; }
        public List<GarminSleepDetailDto>? SleepDetails { get; init; }
        public List<GarminStepsDto>? Steps { get; init; }
    }

    private sealed record GarminWindowDto
    {
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }

        public (DateTime?, DateTime?) ToRange()
        {
            DateTime? start = null;
            DateTime? end = null;

            if (!string.IsNullOrWhiteSpace(StartDate) && DateTime.TryParse(StartDate, out var s))
            {
                start = s;
            }

            if (!string.IsNullOrWhiteSpace(EndDate) && DateTime.TryParse(EndDate, out var e))
            {
                end = e;
            }

            return (start, end);
        }
    }

    private sealed record GarminActivityDetailDto
    {
        [JsonPropertyName("summary")]
        public JsonElement Summary { get; init; }
        [JsonPropertyName("details")]
        public JsonElement Details { get; init; }
        [JsonPropertyName("splitSummaries")]
        public JsonElement SplitSummaries { get; init; }
        [JsonPropertyName("splits")]
        public JsonElement Splits { get; init; }
        [JsonPropertyName("typedSplits")]
        public JsonElement TypedSplits { get; init; }
        [JsonPropertyName("weather")]
        public JsonElement Weather { get; init; }
        [JsonPropertyName("heartRateZones")]
        public JsonElement HeartRateZones { get; init; }
        [JsonPropertyName("gear")]
        public JsonElement Gear { get; init; }
        [JsonPropertyName("exerciseSets")]
        public JsonElement ExerciseSets { get; init; }
        [JsonPropertyName("trackPoints")]
        public List<GarminTrackPointDto>? TrackPoints { get; init; }
        [JsonPropertyName("errors")]
        public Dictionary<string, string>? Errors { get; init; }
        [JsonPropertyName("fetchedAtUtc")]
        public string? FetchedAtUtc { get; init; }
    }

    private sealed record GarminTrackPointDto
    {
        public string? Timestamp { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public double? Altitude { get; init; }
        public double? HeartRate { get; init; }

        public GarminTrackPoint? ToTrackPoint()
        {
            var timestamp = ParseDateTime(Timestamp);
            var hasCoordinates = Latitude.HasValue && Longitude.HasValue;

            if (!hasCoordinates && timestamp is null && !Altitude.HasValue && !HeartRate.HasValue)
            {
                return null;
            }

            return new GarminTrackPoint(timestamp, Latitude, Longitude, Altitude, HeartRate);
        }
    }

    private sealed record GarminConnectActivityDto
    {
        public string? ActivityId { get; init; }
        public string? ActivityName { get; init; }
        public string? StartTime { get; init; }
        public double? DistanceMeters { get; init; }
        public double? DurationSeconds { get; init; }
        public string? ActivityType { get; init; }
        [JsonPropertyName("detail")]
        public GarminActivityDetailDto? Detail { get; init; }

        public GarminConnectActivity? ToActivity()
        {
            if (string.IsNullOrWhiteSpace(ActivityId))
            {
                return null;
            }

            var startTime = ParseDateTime(StartTime);

            GarminActivityDetail? detail = null;
            if (Detail is not null)
            {
                var trackPoints = BuildTrackPoints(Detail);

                detail = new GarminActivityDetail(
                    SerializeJsonElement(Detail.Summary),
                    SerializeJsonElement(Detail.Details),
                    SerializeJsonElement(Detail.SplitSummaries),
                    SerializeJsonElement(Detail.Splits),
                    SerializeJsonElement(Detail.TypedSplits),
                    SerializeJsonElement(Detail.Weather),
                    SerializeJsonElement(Detail.HeartRateZones),
                    SerializeJsonElement(Detail.Gear),
                    SerializeJsonElement(Detail.ExerciseSets),
                    trackPoints,
                    Detail.Errors ?? new Dictionary<string, string>(),
                    ParseDateTime(Detail.FetchedAtUtc)
                );
            }

            return new GarminConnectActivity(
                ActivityId!,
                ActivityName ?? string.Empty,
                startTime,
                DistanceMeters,
                DurationSeconds,
                ActivityType ?? string.Empty,
                detail
            );
        }
    }

    private sealed record GarminSleepDto
    {
        public string? Date { get; init; }
        public double? SleepTimeSeconds { get; init; }
        public double? DeepSleepSeconds { get; init; }
        public double? LightSleepSeconds { get; init; }
        public double? RemSleepSeconds { get; init; }
        public double? AwakeSleepSeconds { get; init; }
        public double? SleepScore { get; init; }
        public string? SleepQualityType { get; init; }
        public string? SleepStartLocal { get; init; }
        public string? SleepEndLocal { get; init; }
        public string? SleepStartGmt { get; init; }
        public string? SleepEndGmt { get; init; }
        public double? SleepRestingHeartRate { get; init; }
        public double? BodyBatteryChange { get; init; }
        public double? AverageRespirationValue { get; init; }
        public double? LowestSpO2Value { get; init; }
        public double? SleepTimeGoalSeconds { get; init; }

        public GarminSleepEntry? ToSleepEntry()
        {
            if (string.IsNullOrWhiteSpace(Date) || !DateTime.TryParse(Date, out var parsedDate))
            {
                return null;
            }

            return new GarminSleepEntry(
                parsedDate,
                SleepTimeSeconds,
                DeepSleepSeconds,
                LightSleepSeconds,
                RemSleepSeconds,
                AwakeSleepSeconds,
                SleepScore,
                SleepQualityType ?? string.Empty,
                ParseDateTime(SleepStartLocal),
                ParseDateTime(SleepEndLocal),
                ParseDateTime(SleepStartGmt),
                ParseDateTime(SleepEndGmt),
                SleepRestingHeartRate,
                BodyBatteryChange,
                AverageRespirationValue,
                LowestSpO2Value,
                SleepTimeGoalSeconds
            );
        }
    }

    private sealed record GarminSleepDetailDto
    {
        public string? Date { get; init; }
        public List<GarminSleepLevelDto>? Levels { get; init; }
        public List<GarminSleepSampleDto>? Movement { get; init; }
        public List<GarminSleepSampleDto>? HeartRate { get; init; }
        public List<GarminSleepSampleDto>? BodyBattery { get; init; }

        public GarminSleepDetail? ToDetail()
        {
            if (string.IsNullOrWhiteSpace(Date) || !DateTime.TryParse(Date, out var parsedDate))
            {
                return null;
            }

            var segments = new List<SleepStageSegment>();
            if (Levels is not null)
            {
                foreach (var level in Levels)
                {
                    if (string.IsNullOrWhiteSpace(level.Stage))
                    {
                        continue;
                    }

                    var start = ParseDateTime(level.StartUtc);
                    var end = ParseDateTime(level.EndUtc);

                    if (start is null || end is null || end <= start)
                    {
                        continue;
                    }

                    segments.Add(new SleepStageSegment(level.Stage, start.Value, end.Value));
                }
            }

            return new GarminSleepDetail(
                parsedDate,
                segments,
                ConvertSamples(Movement),
                ConvertSamples(HeartRate),
                ConvertSamples(BodyBattery)
            );
        }

        private static List<SleepValueSample> ConvertSamples(List<GarminSleepSampleDto>? samples)
        {
            var results = new List<SleepValueSample>();
            if (samples is null)
            {
                return results;
            }

            foreach (var sample in samples)
            {
                var timestamp = ParseDateTime(sample.TimestampUtc);
                if (timestamp is null || sample.Value is null)
                {
                    continue;
                }

                results.Add(new SleepValueSample(timestamp.Value, sample.Value.Value));
            }

            return results;
        }
    }

    private sealed record GarminSleepLevelDto
    {
        public string? Stage { get; init; }
        public string? StartUtc { get; init; }
        public string? EndUtc { get; init; }
    }

    private sealed record GarminSleepSampleDto
    {
        public string? TimestampUtc { get; init; }
        public double? Value { get; init; }
    }

    private sealed record GarminStepsDto
    {
        public string? Date { get; init; }
        public double? TotalSteps { get; init; }
        public double? Goal { get; init; }
        public double? TotalCalories { get; init; }
        public double? ActiveCalories { get; init; }
        public double? TotalDistanceMeters { get; init; }

        public GarminStepsEntry? ToStepsEntry()
        {
            if (string.IsNullOrWhiteSpace(Date) || !DateTime.TryParse(Date, out var parsedDate))
            {
                return null;
            }

            return new GarminStepsEntry(
                parsedDate,
                TotalSteps,
                Goal,
                TotalCalories,
                ActiveCalories,
                TotalDistanceMeters
            );
        }
    }
}

public record GarminConnectActivity(
    string ActivityId,
    string Name,
    DateTime? StartTime,
    double? DistanceMeters,
    double? DurationSeconds,
    string ActivityType,
    GarminActivityDetail? Detail
);

public record GarminTrackPoint(
    DateTime? Timestamp,
    double? Latitude,
    double? Longitude,
    double? Altitude,
    double? HeartRate
);

public record GarminActivityDetail(
    string? SummaryJson,
    string? DetailsJson,
    string? SplitSummariesJson,
    string? SplitsJson,
    string? TypedSplitsJson,
    string? WeatherJson,
    string? HeartRateZonesJson,
    string? GearJson,
    string? ExerciseSetsJson,
    IReadOnlyList<GarminTrackPoint> TrackPoints,
    IReadOnlyDictionary<string, string> Errors,
    DateTime? FetchedAtUtc
);

public record GarminSleepEntry(
    DateTime Date,
    double? TotalSleepSeconds,
    double? DeepSleepSeconds,
    double? LightSleepSeconds,
    double? RemSleepSeconds,
    double? AwakeSeconds,
    double? SleepScore,
    string SleepQualityType,
    DateTime? SleepStartLocal,
    DateTime? SleepEndLocal,
    DateTime? SleepStartGmt,
    DateTime? SleepEndGmt,
    double? RestingHeartRate,
    double? BodyBatteryChange,
    double? AverageRespirationValue,
    double? LowestSpO2Value,
    double? SleepGoalSeconds
);

public record GarminSleepDetail(
    DateTime Date,
    IReadOnlyList<SleepStageSegment> Levels,
    IReadOnlyList<SleepValueSample> Movement,
    IReadOnlyList<SleepValueSample> HeartRate,
    IReadOnlyList<SleepValueSample> BodyBattery
);

public record SleepStageSegment(
    string Stage,
    DateTime StartUtc,
    DateTime EndUtc
);

public record SleepValueSample(
    DateTime TimestampUtc,
    double Value
);

public record GarminStepsEntry(
    DateTime Date,
    double? TotalSteps,
    double? GoalSteps,
    double? TotalCalories,
    double? ActiveCalories,
    double? TotalDistanceMeters
);

public record GarminConnectFetchResult(
    IReadOnlyList<GarminConnectActivity> Activities,
    IReadOnlyList<GarminSleepEntry> Sleep,
    IReadOnlyList<GarminSleepDetail> SleepDetails,
    IReadOnlyList<GarminStepsEntry> Steps,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    string StandardOutput,
    string StandardError
);
