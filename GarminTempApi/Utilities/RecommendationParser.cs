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

            var today = ReadString("today_insight");
            var action = ReadString("action_12h");
            var tomorrow = ReadString("tomorrow_preparation");
            var nutrition = ReadString("nutrition");

            if (string.IsNullOrWhiteSpace(today) ||
                string.IsNullOrWhiteSpace(action) ||
                string.IsNullOrWhiteSpace(tomorrow) ||
                string.IsNullOrWhiteSpace(nutrition))
            {
                return false;
            }

            sections = new DailyRecommendationSections(today!, action!, tomorrow!, nutrition!);
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
            MarkdownRenderer.ToHtml(sections.TodayInsight),
            MarkdownRenderer.ToHtml(sections.Action12h),
            MarkdownRenderer.ToHtml(sections.TomorrowPreparation),
            MarkdownRenderer.ToHtml(sections.Nutrition));
    }

    public static string CombineHtml(DailyRecommendationHtmlSections htmlSections)
    {
        return string.Join(Environment.NewLine,
            new[]
            {
                htmlSections.TodayInsight,
                htmlSections.Action12h,
                htmlSections.TomorrowPreparation,
                htmlSections.Nutrition
            }.Where(static block => !string.IsNullOrWhiteSpace(block)));
    }
}
