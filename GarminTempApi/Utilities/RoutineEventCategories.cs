using System;
using System.Collections.Generic;
using System.Linq;

namespace GarminTempApi.Utilities;

internal static class RoutineEventCategories
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "sedentary tasks (work, study, meetings, desk time)",
        "low effort tasks (easy walks, errands, light movement)",
        "high effort tasks (manual labor, physically demanding non-workout activities)",
        "workout strength",
        "workout intense",
        "workout cardio",
        "race"
    };

    private static readonly Dictionary<string, string> LegacyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["training"] = "workout cardio",
        ["workout"] = "workout intense",
        ["recovery"] = "low effort tasks (easy walks, errands, light movement)",
        ["rest"] = "sedentary tasks (work, study, meetings, desk time)",
        ["rest day"] = "sedentary tasks (work, study, meetings, desk time)",
        ["race"] = "race"
    };

    public static string Normalize(string? classification)
    {
        if (!string.IsNullOrWhiteSpace(classification))
        {
            var trimmed = classification.Trim();
            foreach (var value in All)
            {
                if (string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            if (LegacyMap.TryGetValue(trimmed, out var mapped))
            {
                return mapped;
            }
        }

        return All[5];
    }

    public static bool IsRace(string? classification)
    {
        return string.Equals(classification, "race", StringComparison.OrdinalIgnoreCase);
    }
}
