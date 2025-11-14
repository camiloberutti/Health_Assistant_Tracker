using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using GarminTempApi.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Controllers;

[ApiController]
[Route("api/routine-events")]
public class RoutineEventsController : ControllerBase
{
    private readonly AppDbContext _db;

    public RoutineEventsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RoutineEventDto>>> GetRange([FromQuery] DateOnly? startDate, [FromQuery] DateOnly? endDate)
    {
        var start = startDate ?? DateOnly.FromDateTime(DateTime.Today.StartOfWeek(DayOfWeek.Monday));
        var end = endDate ?? start.AddDays(7);

        if (end < start)
        {
            return BadRequest("endDate must be on or after startDate.");
        }

        var rangeStartLocal = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var rangeEndExclusive = end.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);

        var candidates = await _db.RoutineEvents
            .Where(e =>
                (!e.IsRecurring && e.StartLocal < rangeEndExclusive && e.EndLocal >= rangeStartLocal) ||
                (e.IsRecurring &&
                 (e.RecurrenceEndLocal ?? DateTime.MaxValue) >= rangeStartLocal &&
                 (e.RecurrenceStartLocal ?? e.StartLocal) < rangeEndExclusive))
            .ToListAsync();

        var occurrences = new List<RoutineEventDto>(capacity: candidates.Count);

        foreach (var entity in candidates)
        {
            if (entity.IsRecurring && !string.IsNullOrWhiteSpace(entity.RecurrenceDays))
            {
                occurrences.AddRange(ExpandRecurringOccurrences(entity, start, end));
            }
            else if (entity.EndLocal >= rangeStartLocal && entity.StartLocal < rangeEndExclusive)
            {
                occurrences.Add(RoutineEventDto.FromEntity(entity));
            }
        }

        occurrences.Sort((a, b) => a.StartLocal.CompareTo(b.StartLocal));
        return Ok(occurrences);
    }

    [HttpPost]
    public async Task<ActionResult<RoutineEventDto>> Create([FromBody] RoutineEventRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var now = DateTime.UtcNow;
        var entity = new RoutineEvent
        {
            Title = request.Title.Trim(),
            Classification = request.Classification,
            StartLocal = request.StartLocal.AsLocalTime(),
            EndLocal = request.EndLocal.AsLocalTime(),
            IsRace = request.IsRace,
            RaceName = request.RaceName?.Trim(),
            RaceLocation = request.RaceLocation?.Trim(),
            RaceGoal = request.RaceGoal?.Trim(),
            Notes = request.Notes?.Trim(),
            CreatedUtc = now,
            UpdatedUtc = now
        };

    var recurrenceDays = RoutineEventToolkit.NormalizeDays(request.RecurrenceDays);
        DateOnly? recurrenceStart = null;
        DateOnly? recurrenceEnd = null;

        if (request.RepeatWeekly)
        {
            if (recurrenceDays.Count == 0)
            {
                return BadRequest("Recurring events require at least one selected weekday.");
            }

            if (!TryParseDateOnly(request.RecurrenceStartDate, out recurrenceStart))
            {
                return BadRequest("Invalid recurrence start date.");
            }

            if (!TryParseDateOnly(request.RecurrenceEndDate, out recurrenceEnd))
            {
                return BadRequest("Invalid recurrence end date.");
            }

            recurrenceStart ??= DateOnly.FromDateTime(entity.StartLocal.AsLocalTime());
            recurrenceEnd ??= recurrenceStart;

            if (recurrenceEnd < recurrenceStart)
            {
                return BadRequest("Recurrence end date must be on or after the recurrence start date.");
            }
        }
        else
        {
            recurrenceDays = Array.Empty<int>();
        }

        if (entity.EndLocal <= entity.StartLocal)
        {
            return BadRequest("End time must be after start time.");
        }

        if (entity.IsRace && (string.IsNullOrWhiteSpace(entity.RaceName) || string.IsNullOrWhiteSpace(entity.RaceLocation) || string.IsNullOrWhiteSpace(entity.RaceGoal)))
        {
            return BadRequest("Race events require name, location, and goal.");
        }

    entity.IsRecurring = request.RepeatWeekly && recurrenceDays.Count > 0;
        if (entity.IsRecurring)
        {
            var startBoundary = recurrenceStart ?? DateOnly.FromDateTime(entity.StartLocal.AsLocalTime());
            var endBoundary = recurrenceEnd ?? startBoundary;

            entity.RecurrenceDays = RoutineEventToolkit.SerializeDays(recurrenceDays);
            entity.RecurrenceStartLocal = RoutineEventToolkit.ToLocalMidnight(startBoundary);
            entity.RecurrenceEndLocal = RoutineEventToolkit.ToLocalMidnight(endBoundary);
        }
        else
        {
            entity.RecurrenceDays = null;
            entity.RecurrenceStartLocal = null;
            entity.RecurrenceEndLocal = null;
        }

        _db.RoutineEvents.Add(entity);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, RoutineEventDto.FromEntity(entity));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<RoutineEventDto>> GetById(int id)
    {
        var entity = await _db.RoutineEvents.FindAsync(id);
        if (entity is null)
        {
            return NotFound();
        }

        return Ok(RoutineEventDto.FromEntity(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<RoutineEventDto>> Update(int id, [FromBody] RoutineEventRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var entity = await _db.RoutineEvents.FindAsync(id);
        if (entity is null)
        {
            return NotFound();
        }

        var newStart = request.StartLocal.AsLocalTime();
        var newEnd = request.EndLocal.AsLocalTime();
        if (newEnd <= newStart)
        {
            return BadRequest("End time must be after start time.");
        }

        entity.Title = request.Title.Trim();
        entity.Classification = request.Classification;
        entity.StartLocal = newStart;
        entity.EndLocal = newEnd;
        entity.IsRace = request.IsRace;
        entity.RaceName = request.RaceName?.Trim();
        entity.RaceLocation = request.RaceLocation?.Trim();
        entity.RaceGoal = request.RaceGoal?.Trim();
        entity.Notes = request.Notes?.Trim();
        entity.UpdatedUtc = DateTime.UtcNow;

    var updateRecurrenceDays = RoutineEventToolkit.NormalizeDays(request.RecurrenceDays);
        DateOnly? updateRecurrenceStart = null;
        DateOnly? updateRecurrenceEnd = null;

        if (request.RepeatWeekly)
        {
            if (updateRecurrenceDays.Count == 0)
            {
                return BadRequest("Recurring events require at least one selected weekday.");
            }

            if (!TryParseDateOnly(request.RecurrenceStartDate, out updateRecurrenceStart))
            {
                return BadRequest("Invalid recurrence start date.");
            }

            if (!TryParseDateOnly(request.RecurrenceEndDate, out updateRecurrenceEnd))
            {
                return BadRequest("Invalid recurrence end date.");
            }

            updateRecurrenceStart ??= DateOnly.FromDateTime(entity.StartLocal.AsLocalTime());
            updateRecurrenceEnd ??= updateRecurrenceStart;

            if (updateRecurrenceEnd < updateRecurrenceStart)
            {
                return BadRequest("Recurrence end date must be on or after the recurrence start date.");
            }
        }
        else
        {
            updateRecurrenceDays = Array.Empty<int>();
        }

        if (entity.IsRace && (string.IsNullOrWhiteSpace(entity.RaceName) || string.IsNullOrWhiteSpace(entity.RaceLocation) || string.IsNullOrWhiteSpace(entity.RaceGoal)))
        {
            return BadRequest("Race events require name, location, and goal.");
        }

    entity.IsRecurring = request.RepeatWeekly && updateRecurrenceDays.Count > 0;
        if (entity.IsRecurring)
        {
            var startBoundary = updateRecurrenceStart ?? DateOnly.FromDateTime(entity.StartLocal.AsLocalTime());
            var endBoundary = updateRecurrenceEnd ?? startBoundary;

            entity.RecurrenceDays = RoutineEventToolkit.SerializeDays(updateRecurrenceDays);
            entity.RecurrenceStartLocal = RoutineEventToolkit.ToLocalMidnight(startBoundary);
            entity.RecurrenceEndLocal = RoutineEventToolkit.ToLocalMidnight(endBoundary);
        }
        else
        {
            entity.RecurrenceDays = null;
            entity.RecurrenceStartLocal = null;
            entity.RecurrenceEndLocal = null;
        }

        await _db.SaveChangesAsync();

        return Ok(RoutineEventDto.FromEntity(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.RoutineEvents.FindAsync(id);
        if (entity is null)
        {
            return NotFound();
        }

        _db.RoutineEvents.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    public sealed record RoutineEventRequest
    {
        [Required]
        [StringLength(200)]
        public string Title { get; init; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Classification { get; init; } = "Training";

        [Required]
        public DateTime StartLocal { get; init; }

        [Required]
        public DateTime EndLocal { get; init; }

        public bool IsRace { get; init; }

        public bool RepeatWeekly { get; init; }

        public List<int> RecurrenceDays { get; init; } = new();

        public string? RecurrenceStartDate { get; init; }

        public string? RecurrenceEndDate { get; init; }

        [StringLength(200)]
        public string? RaceName { get; init; }

        [StringLength(200)]
        public string? RaceLocation { get; init; }

        [StringLength(400)]
        public string? RaceGoal { get; init; }

        [StringLength(1000)]
        public string? Notes { get; init; }
    }

    public sealed record RoutineEventDto
    {
        public int Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Classification { get; init; } = string.Empty;
        public DateTime StartLocal { get; init; }
        public DateTime EndLocal { get; init; }
        public bool IsRace { get; init; }
        public bool IsRecurring { get; init; }
        public IReadOnlyList<int> RecurrenceDays { get; init; } = Array.Empty<int>();
        public string? RecurrenceStartDate { get; init; }
        public string? RecurrenceEndDate { get; init; }
        public string? RaceName { get; init; }
        public string? RaceLocation { get; init; }
        public string? RaceGoal { get; init; }
        public string? Notes { get; init; }

        public static RoutineEventDto FromEntity(RoutineEvent entity) => FromOccurrence(entity, entity.StartLocal, entity.EndLocal, null);

        public static RoutineEventDto FromOccurrence(RoutineEvent entity, DateTime startLocal, DateTime endLocal, IReadOnlyList<int>? recurrenceDays)
        {
            var days = recurrenceDays ?? RoutineEventToolkit.ParseDays(entity.RecurrenceDays);
            var isRecurring = entity.IsRecurring && days.Count > 0;

            return new RoutineEventDto
            {
                Id = entity.Id,
                Title = entity.Title,
                Classification = entity.Classification,
                StartLocal = startLocal.AsLocalTime(),
                EndLocal = endLocal.AsLocalTime(),
                IsRace = entity.IsRace,
                IsRecurring = isRecurring,
                RecurrenceDays = isRecurring ? days : Array.Empty<int>(),
                RecurrenceStartDate = isRecurring ? FormatDate(entity.RecurrenceStartLocal) : null,
                RecurrenceEndDate = isRecurring ? FormatDate(entity.RecurrenceEndLocal) : null,
                RaceName = entity.RaceName,
                RaceLocation = entity.RaceLocation,
                RaceGoal = entity.RaceGoal,
                Notes = entity.Notes
            };
        }

        private static string? FormatDate(DateTime? value)
        {
            if (value is null)
            {
                return null;
            }

            var local = value.Value.AsLocalTime();
            return DateOnly.FromDateTime(local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    private static IEnumerable<RoutineEventDto> ExpandRecurringOccurrences(RoutineEvent entity, DateOnly rangeStart, DateOnly rangeEndExclusive)
    {
        foreach (var occurrence in RoutineEventToolkit.ExpandOccurrences(entity, rangeStart, rangeEndExclusive))
        {
            yield return RoutineEventDto.FromOccurrence(entity, occurrence.StartLocal, occurrence.EndLocal, occurrence.RecurrenceDays);
        }
    }

    private static bool TryParseDateOnly(string? literal, out DateOnly? date)
    {
        if (string.IsNullOrWhiteSpace(literal))
        {
            date = null;
            return true;
        }

        if (DateOnly.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            date = parsedDate;
            return true;
        }

        if (DateTime.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedDateTime))
        {
            date = DateOnly.FromDateTime(parsedDateTime);
            return true;
        }

        date = null;
        return false;
    }

}

