using System;

namespace GarminTempApi.Utilities;

internal static class DateTimeExtensions
{
    public static DateTime AsLocalTime(this DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Local => value,
            DateTimeKind.Utc => value.ToLocalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local)
        };
    }

    public static DateTime StartOfWeek(this DateTime date, DayOfWeek startOfWeek)
    {
        var diff = (7 + (date.DayOfWeek - startOfWeek)) % 7;
        return date.Date.AddDays(-diff);
    }
}
