using System;
using System.Linq;
using System.Text.Json;
using GarminTempApi.Services;

namespace GarminTempApi.Utilities;

internal static class RecommendationParser
{
    public static bool TryParseSections(string? content, out DailyRecommendationSections? sections)
    {
        sections = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? ReadString(string property)
            {
                return root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString()?.Trim()
                    : null;
            }

            var recent_summary = ReadString("recent_summary");
            var upcoming_outlook = ReadString("upcoming_outlook");
            var suggested_workout = ReadString("suggested_workout");

            if (string.IsNullOrWhiteSpace(recent_summary) ||
                string.IsNullOrWhiteSpace(upcoming_outlook))
            {
                return false;
            }

            sections = new DailyRecommendationSections(recent_summary!, upcoming_outlook!, suggested_workout);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static DailyRecommendationHtmlSections ToHtmlSections(DailyRecommendationSections sections)
    {
        return new DailyRecommendationHtmlSections(
            MarkdownRenderer.ToHtml(sections.RecentSummary),
            MarkdownRenderer.ToHtml(sections.UpcomingOutlook),
            string.IsNullOrWhiteSpace(sections.SuggestedWorkout) ? null : MarkdownRenderer.ToHtml(sections.SuggestedWorkout));
    }

    public static string CombineHtml(DailyRecommendationHtmlSections htmlSections)
    {
        return string.Join(Environment.NewLine,
            new[]
            {
                htmlSections.RecentSummary,
                htmlSections.UpcomingOutlook
            }.Where(static block => !string.IsNullOrWhiteSpace(block)));
    }
}
