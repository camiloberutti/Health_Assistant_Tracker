using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using GarminTempApi.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace GarminTempApi.Plugins;

/// <summary>
/// Semantic Kernel plugin that lets the AI read and write to the user's routine calendar.
/// This is the key enabler for "Dynamic Routine Incorporation" — the AI can propose
/// and actually add workouts to the calendar.
/// </summary>
public sealed class CalendarPlugin
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CalendarPlugin> _logger;

    public CalendarPlugin(IServiceProvider serviceProvider, ILogger<CalendarPlugin> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    [KernelFunction("get_upcoming_events")]
    [Description("Gets the user's upcoming routine/calendar events for the next N days. Returns event title, date/time, classification (e.g. 'workout cardio', 'race'), and whether it's a race.")]
    public async Task<string> GetUpcomingEventsAsync(
        [Description("Number of days ahead to look. Default is 7.")] int days = 7,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 60);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var today = DateTime.Now.Date;
        var rangeEnd = today.AddDays(days);

        var entities = await db.RoutineEvents
            .Where(e =>
                (!e.IsRecurring && e.StartLocal >= today && e.StartLocal < rangeEnd) ||
                (e.IsRecurring &&
                 (e.RecurrenceEndLocal ?? DateTime.MaxValue) >= today &&
                 (e.RecurrenceStartLocal ?? e.StartLocal) < rangeEnd))
            .ToListAsync(cancellationToken);

        var results = new List<object>();
        var startDate = DateOnly.FromDateTime(today);
        var endDate = DateOnly.FromDateTime(rangeEnd);

        foreach (var entity in entities)
        {
            if (entity.IsRecurring && !string.IsNullOrWhiteSpace(entity.RecurrenceDays))
            {
                foreach (var occ in RoutineEventToolkit.ExpandOccurrences(entity, startDate, endDate))
                {
                    results.Add(new
                    {
                        id = entity.Id,
                        title = entity.Title,
                        date = occ.StartLocal.ToString("yyyy-MM-dd"),
                        startTime = occ.StartLocal.ToString("HH:mm"),
                        endTime = occ.EndLocal.ToString("HH:mm"),
                        classification = entity.Classification,
                        isRace = entity.IsRace,
                        notes = entity.Notes
                    });
                }
            }
            else
            {
                results.Add(new
                {
                    id = entity.Id,
                    title = entity.Title,
                    date = entity.StartLocal.ToString("yyyy-MM-dd"),
                    startTime = entity.StartLocal.ToString("HH:mm"),
                    endTime = entity.EndLocal.ToString("HH:mm"),
                    classification = entity.Classification,
                    isRace = entity.IsRace,
                    notes = entity.Notes
                });
            }
        }

        if (results.Count == 0)
            return $"No events scheduled in the next {days} days.";

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = false });
    }

    [KernelFunction("add_workout_to_calendar")]
    [Description("Adds a new workout or event to the user's routine calendar. Use this when you want to suggest a workout and the user agrees, or when proactively scheduling a recovery session. ALWAYS explain what you're adding before calling this.")]
    public async Task<string> AddWorkoutToCalendarAsync(
        [Description("Title of the workout, e.g. 'Easy Recovery Run', 'Interval Training', 'Rest Day Yoga'")] string title,
        [Description("The date for the workout in yyyy-MM-dd format")] string date,
        [Description("Start time in HH:mm format (24h), e.g. '07:00'")] string startTime,
        [Description("End time in HH:mm format (24h), e.g. '08:00'")] string endTime,
        [Description("Classification: 'workout cardio', 'workout strength', 'workout flexibility', 'recovery', 'race', or 'other'")] string classification = "workout cardio",
        [Description("Optional notes about the workout (intensity, goals, etc.)")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "Error: Title is required.";
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(startTime) || string.IsNullOrWhiteSpace(endTime))
            return "Error: Date, start time, and end time are required.";

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            return $"Error: Invalid date format '{date}'. Use yyyy-MM-dd.";
        if (!TimeOnly.TryParseExact(startTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart))
            return $"Error: Invalid start time format '{startTime}'. Use HH:mm.";
        if (!TimeOnly.TryParseExact(endTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd))
            return $"Error: Invalid end time format '{endTime}'. Use HH:mm.";

        var startLocal = parsedDate.ToDateTime(parsedStart, DateTimeKind.Local);
        var endLocal = parsedDate.ToDateTime(parsedEnd, DateTimeKind.Local);

        if (endLocal <= startLocal)
            return "Error: End time must be after start time.";

        var normalizedClassification = RoutineEventCategories.Normalize(classification);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = new RoutineEvent
        {
            Title = title.Trim(),
            Classification = normalizedClassification,
            StartLocal = startLocal,
            EndLocal = endLocal,
            IsRace = RoutineEventCategories.IsRace(normalizedClassification),
            Notes = notes?.Trim(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.RoutineEvents.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("AI added workout to calendar: {Title} on {Date} ({Start}-{End})",
            title, date, startTime, endTime);

        return $"Successfully added '{title}' to your calendar on {date} from {startTime} to {endTime}.";
    }

    [KernelFunction("check_schedule_conflicts")]
    [Description("Checks if a specific date and time slot has any conflicting events, including recurring events. Use this before adding a workout to avoid double-booking.")]
    public async Task<string> CheckScheduleConflictsAsync(
        [Description("The date to check in yyyy-MM-dd format")] string date,
        [Description("Start time in HH:mm format")] string startTime,
        [Description("End time in HH:mm format")] string endTime,
        CancellationToken cancellationToken = default)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            return $"Error: Invalid date format '{date}'.";
        if (!TimeOnly.TryParseExact(startTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart))
            return $"Error: Invalid start time format '{startTime}'.";
        if (!TimeOnly.TryParseExact(endTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd))
            return $"Error: Invalid end time format '{endTime}'.";

        var startLocal = parsedDate.ToDateTime(parsedStart, DateTimeKind.Local);
        var endLocal = parsedDate.ToDateTime(parsedEnd, DateTimeKind.Local);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dayStart = parsedDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var dayEnd = parsedDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);

        // Fetch all events that could possibly fall on this date (one-off AND recurring)
        var entities = await db.RoutineEvents
            .Where(e =>
                (!e.IsRecurring && e.StartLocal >= dayStart && e.StartLocal < dayEnd) ||
                (e.IsRecurring &&
                 (e.RecurrenceEndLocal ?? DateTime.MaxValue) >= dayStart &&
                 (e.RecurrenceStartLocal ?? e.StartLocal) < dayEnd))
            .ToListAsync(cancellationToken);

        var conflicts = new List<object>();
        var checkDate = DateOnly.FromDateTime(dayStart);
        var checkDateEnd = DateOnly.FromDateTime(dayEnd);

        foreach (var entity in entities)
        {
            if (entity.IsRecurring && !string.IsNullOrWhiteSpace(entity.RecurrenceDays))
            {
                foreach (var occ in RoutineEventToolkit.ExpandOccurrences(entity, checkDate, checkDateEnd))
                {
                    if (occ.StartLocal < endLocal && occ.EndLocal > startLocal)
                    {
                        conflicts.Add(new { entity.Title, start = occ.StartLocal.ToString("HH:mm"), end = occ.EndLocal.ToString("HH:mm"), recurring = true });
                    }
                }
            }
            else if (!entity.IsRecurring)
            {
                if (entity.StartLocal < endLocal && entity.EndLocal > startLocal)
                {
                    conflicts.Add(new { entity.Title, start = entity.StartLocal.ToString("HH:mm"), end = entity.EndLocal.ToString("HH:mm"), recurring = false });
                }
            }
        }

        if (conflicts.Count == 0)
            return $"No conflicts found on {date} between {startTime} and {endTime}. The time slot is free.";

        return $"Conflicts found on {date}: {JsonSerializer.Serialize(conflicts)}";
    }

    [KernelFunction("update_event_in_calendar")]
    [Description("Updates an existing non-recurring event in the calendar. Provide the event ID and the fields to update. Only non-recurring events can be updated.")]
    public async Task<string> UpdateEventInCalendarAsync(
        [Description("The ID of the event to update")] int eventId,
        [Description("New title, or empty to keep current")] string? title = null,
        [Description("New date in yyyy-MM-dd format, or empty to keep current")] string? date = null,
        [Description("New start time in HH:mm format, or empty to keep current")] string? startTime = null,
        [Description("New end time in HH:mm format, or empty to keep current")] string? endTime = null,
        [Description("New classification, or empty to keep current")] string? classification = null,
        [Description("New notes, or empty to keep current")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = await db.RoutineEvents.FindAsync(new object[] { eventId }, cancellationToken);
        if (entity == null)
            return $"Error: Event with ID {eventId} not found.";
        if (entity.IsRecurring)
            return $"Error: Cannot update recurring event '{entity.Title}' (ID {eventId}). Recurring events must be managed from the routine page.";

        if (!string.IsNullOrWhiteSpace(title))
            entity.Title = title.Trim();

        if (!string.IsNullOrWhiteSpace(date) || !string.IsNullOrWhiteSpace(startTime) || !string.IsNullOrWhiteSpace(endTime))
        {
            var currentDate = DateOnly.FromDateTime(entity.StartLocal);
            var currentStartTime = TimeOnly.FromDateTime(entity.StartLocal);
            var currentEndTime = TimeOnly.FromDateTime(entity.EndLocal);

            if (!string.IsNullOrWhiteSpace(date))
            {
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    return $"Error: Invalid date format '{date}'.";
                currentDate = parsedDate;
            }
            if (!string.IsNullOrWhiteSpace(startTime))
            {
                if (!TimeOnly.TryParseExact(startTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart))
                    return $"Error: Invalid start time format '{startTime}'.";
                currentStartTime = parsedStart;
            }
            if (!string.IsNullOrWhiteSpace(endTime))
            {
                if (!TimeOnly.TryParseExact(endTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd))
                    return $"Error: Invalid end time format '{endTime}'.";
                currentEndTime = parsedEnd;
            }

            entity.StartLocal = currentDate.ToDateTime(currentStartTime, DateTimeKind.Local);
            entity.EndLocal = currentDate.ToDateTime(currentEndTime, DateTimeKind.Local);

            if (entity.EndLocal <= entity.StartLocal)
                return "Error: End time must be after start time.";
        }

        if (!string.IsNullOrWhiteSpace(classification))
        {
            entity.Classification = RoutineEventCategories.Normalize(classification);
            entity.IsRace = RoutineEventCategories.IsRace(entity.Classification);
        }

        if (notes != null)
            entity.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        entity.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("AI updated event {EventId}: {Title}", eventId, entity.Title);
        return $"Successfully updated event '{entity.Title}' (ID {eventId}).";
    }

    [KernelFunction("remove_event_from_calendar")]
    [Description("Removes an event from the calendar by its ID. Only non-recurring events can be removed by the AI. ALWAYS confirm with the user before removing an event.")]
    public async Task<string> RemoveEventFromCalendarAsync(
        [Description("The ID of the event to remove")] int eventId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = await db.RoutineEvents.FindAsync(new object[] { eventId }, cancellationToken);
        if (entity == null)
            return $"Error: Event with ID {eventId} not found.";
        if (entity.IsRecurring)
            return $"Error: Cannot remove recurring event '{entity.Title}' (ID {eventId}). Recurring events must be managed from the routine page.";

        var title = entity.Title;
        var dateStr = entity.StartLocal.ToString("yyyy-MM-dd");

        db.RoutineEvents.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("AI removed event {EventId}: {Title} on {Date}", eventId, title, dateStr);
        return $"Successfully removed '{title}' (ID {eventId}) from {dateStr}.";
    }
}
