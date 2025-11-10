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

        var steps = (payload.Steps ?? new List<GarminStepsDto>())
            .Select(dto => dto.ToStepsEntry())
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        var (windowStart, windowEnd) = payload.Window?.ToRange() ?? (null, null);

        return new GarminConnectFetchResult(activities, sleep, steps, windowStart, windowEnd, stdout, stderr);
    }

    private (string? Username, string? Password) GetCredentials()
    {
        var username = GetConfigValue("Username") ?? Environment.GetEnvironmentVariable("GARMIN_USERNAME");
        var password = GetConfigValue("Password") ?? Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
        return (username, password);
    }

    private string? GetConfigValue(string key) => _config[$"GarminConnect:{key}"];

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

    private sealed record GarminFetchPayload
    {
        public GarminWindowDto? Window { get; init; }
        public List<GarminConnectActivityDto>? Activities { get; init; }
        public List<GarminSleepDto>? Sleep { get; init; }
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
                var trackPoints = Detail.TrackPoints is not null
                    ? Detail.TrackPoints
                        .Select(tp => tp.ToTrackPoint())
                        .Where(tp => tp is not null)
                        .Select(tp => tp!)
                        .ToList()
                    : new List<GarminTrackPoint>();

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
                SleepQualityType ?? string.Empty
            );
        }
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
    string SleepQualityType
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
    IReadOnlyList<GarminStepsEntry> Steps,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    string StandardOutput,
    string StandardError
);
