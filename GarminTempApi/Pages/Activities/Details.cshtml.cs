using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    private readonly AppDbContext _db;

    public DetailsModel(AppDbContext db) => _db = db;

    public ActivityDetail? Activity { get; private set; }
    public IReadOnlyList<ActivityPoint> TrackPoints { get; private set; } = Array.Empty<ActivityPoint>();
    public GarminActivityDetail? Detail { get; private set; }
    public IReadOnlyList<(string Label, string Value)> SummaryMetrics { get; private set; } = Array.Empty<(string, string)>();
    public IReadOnlyList<DetailJsonBlock> JsonBlocks { get; private set; } = Array.Empty<DetailJsonBlock>();
    public IReadOnlyDictionary<string, string> DetailErrors { get; private set; } = new Dictionary<string, string>();

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

        TrackPoints = await _db.ActivityPoints
            .AsNoTracking()
            .Where(p => p.ActivityId == Activity.ActivityDbId)
            .OrderBy(p => p.Timestamp)
            .Take(500)
            .ToListAsync();

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

    public sealed record DetailJsonBlock(string Title, string Json);
}
