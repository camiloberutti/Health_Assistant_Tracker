using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GarminTempApi.Models;

namespace GarminTempApi.Utilities;

internal static class RoutineEventToolkit
{
    internal readonly record struct RoutineEventOccurrence(
        RoutineEvent Event,
        DateTime StartLocal,
        DateTime EndLocal,
        IReadOnlyList<int> RecurrenceDays);

    internal static IReadOnlyList<int> NormalizeDays(IEnumerable<int>? days)
    {
        if (days is null)
        {
            return Array.Empty<int>();
        }

        var set = new SortedSet<int>();
        foreach (var day in days)
        {
            if (day is >= 0 and <= 6)
            {
                set.Add(day);
            }
        }

        return set.Count == 0 ? Array.Empty<int>() : set.ToArray();
    }

    internal static string SerializeDays(IEnumerable<int> days)
    {
        return string.Join(',', days.Select(d => d.ToString(CultureInfo.InvariantCulture)));
    }

    internal static IReadOnlyList<int> ParseDays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        var set = new SortedSet<int>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) && day is >= 0 and <= 6)
            {
                set.Add(day);
            }
        }

        return set.Count == 0 ? Array.Empty<int>() : set.ToArray();
    }

    internal static DateTime ToLocalMidnight(DateOnly date)
    {
        return DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);
    }

    internal static IEnumerable<RoutineEventOccurrence> ExpandOccurrences(RoutineEvent entity, DateOnly rangeStart, DateOnly rangeEndExclusive)
    {
        var days = ParseDays(entity.RecurrenceDays);
        if (days.Count == 0)
        {
            yield break;
        }

        var daySet = new HashSet<int>(days);
        var localStart = entity.StartLocal.AsLocalTime();
        var localEnd = entity.EndLocal.AsLocalTime();
        var duration = localEnd - localStart;
        if (duration <= TimeSpan.Zero)
        {
            yield return new RoutineEventOccurrence(entity, localStart, localEnd, days);
            yield break;
        }

        var recurrenceStart = entity.RecurrenceStartLocal is { } startBoundary
            ? DateOnly.FromDateTime(startBoundary.AsLocalTime())
            : DateOnly.FromDateTime(localStart);

        var recurrenceEnd = entity.RecurrenceEndLocal is { } endBoundary
            ? DateOnly.FromDateTime(endBoundary.AsLocalTime())
            : DateOnly.MaxValue;

        var inclusiveRangeEnd = rangeEndExclusive > DateOnly.MinValue
            ? rangeEndExclusive.AddDays(-1)
            : rangeEndExclusive;

        var iterationStart = MaxDate(rangeStart, recurrenceStart);
        var iterationEnd = MinDate(inclusiveRangeEnd, recurrenceEnd);

        if (iterationStart > iterationEnd)
        {
            yield break;
        }

        var startTime = TimeOnly.FromDateTime(localStart);
        var current = iterationStart;

        while (current <= iterationEnd)
        {
            if (daySet.Contains((int)current.DayOfWeek))
            {
                var occurrenceStart = DateTime.SpecifyKind(current.ToDateTime(startTime), DateTimeKind.Local);
                var occurrenceEnd = occurrenceStart + duration;
                yield return new RoutineEventOccurrence(entity, occurrenceStart, occurrenceEnd, days);
            }

            if (current == DateOnly.MaxValue)
            {
                break;
            }

            current = current.AddDays(1);
        }
    }

    private static DateOnly MaxDate(DateOnly left, DateOnly right) => left > right ? left : right;

    private static DateOnly MinDate(DateOnly left, DateOnly right) => left < right ? left : right;
}
