using System;
using System.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;

namespace FloatingTodo;

public sealed class ICalendarCodec
{
    private const string ProductId = "-//FloatingTodo//CalDAV VTODO 1.0//EN";

    public TodoItem Parse(string rawICalendar, string calendarId, string remoteHref, string? etag, Guid? localId = null)
    {
        var calendar = IcalCalendar.Load(rawICalendar) ?? throw new FormatException("无法解析 iCalendar 资源");
        var todo = calendar.Todos.FirstOrDefault(candidate => candidate.RecurrenceIdentifier is null)
                   ?? calendar.Todos.FirstOrDefault()
                   ?? throw new FormatException("iCalendar 资源不包含 VTODO");
        var due = FromCalDateTime(todo.Due, out var hasDueTime);
        var start = FromCalDateTime(todo.DtStart, out _);
        DateTimeOffset? completed = todo.Completed is null
            ? null
            : new DateTimeOffset(todo.Completed.AsUtc, TimeSpan.Zero);
        var recurrence = todo.RecurrenceRule?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(recurrence))
        {
            var latestCompleted = calendar.Todos
                .Where(candidate => candidate.RecurrenceIdentifier is not null &&
                                    string.Equals(candidate.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.RecurrenceIdentifier!.StartTime.Value)
                .FirstOrDefault();
            if (latestCompleted is not null)
            {
                var next = FindNextOccurrence(calendar, latestCompleted.RecurrenceIdentifier!.StartTime);
                if (next is null)
                {
                    completed = latestCompleted.Completed is null
                        ? DateTimeOffset.Now
                        : new DateTimeOffset(latestCompleted.Completed.AsUtc, TimeSpan.Zero);
                }
                else
                {
                    start = todo.DtStart is null ? null : FromCalDateTime(next, out _);
                    due = ShiftDue(todo, next, out hasDueTime);
                }
            }
        }

        return new TodoItem
        {
            Id = localId ?? Guid.NewGuid(),
            Uid = string.IsNullOrWhiteSpace(todo.Uid) ? Guid.NewGuid().ToString("D") : todo.Uid,
            ListId = calendarId,
            RemoteHref = remoteHref,
            ETag = etag,
            RawICalendar = rawICalendar,
            BaseICalendar = rawICalendar,
            Title = todo.Summary ?? "未命名任务",
            Description = todo.Description ?? string.Empty,
            Start = start,
            Due = due,
            HasDueTime = hasDueTime,
            Priority = FromRfcPriority(todo.Priority),
            RecurrenceRule = recurrence,
            CompletedAt = completed,
            UpdatedAt = todo.LastModified is null
                ? DateTimeOffset.Now
                : new DateTimeOffset(todo.LastModified.AsUtc, TimeSpan.Zero)
        };
    }

    public string Serialize(TodoItem task)
    {
        IcalCalendar calendar;
        Todo todo;
        if (!string.IsNullOrWhiteSpace(task.RawICalendar))
        {
            calendar = IcalCalendar.Load(task.RawICalendar) ?? throw new FormatException("无法解析任务的原始 iCalendar 数据");
            todo = calendar.Todos.FirstOrDefault(candidate => candidate.RecurrenceIdentifier is null) ?? calendar.Create<Todo>();
        }
        else
        {
            calendar = new IcalCalendar { ProductId = ProductId, Version = "2.0" };
            todo = calendar.Create<Todo>();
        }

        todo.Uid = string.IsNullOrWhiteSpace(task.Uid) ? Guid.NewGuid().ToString("D") : task.Uid;
        todo.Summary = task.Title;
        todo.Description = task.Description;
        todo.DtStart = ToCalDateTime(task.Start, task.Start is not null);
        todo.Due = ToCalDateTime(task.Due, task.HasDueTime);
        todo.Priority = ToRfcPriority(task.Priority);
        todo.DtStamp = CalDateTime.UtcNow;
        todo.LastModified = CalDateTime.UtcNow;
        todo.RecurrenceRule = string.IsNullOrWhiteSpace(task.RecurrenceRule)
            ? null
            : new RecurrenceRule(task.RecurrenceRule.Trim());

        if (task.CompletedAt is not null)
        {
            todo.Status = "COMPLETED";
            todo.PercentComplete = 100;
            todo.Completed = new CalDateTime(task.CompletedAt.Value.UtcDateTime, true);
        }
        else
        {
            todo.Status = "NEEDS-ACTION";
            todo.PercentComplete = 0;
            todo.Completed = null!;
        }

        return new CalendarSerializer(calendar).SerializeToString()
               ?? throw new InvalidOperationException("无法序列化 iCalendar 任务");
    }

    public void ValidateRecurrence(string recurrence)
    {
        foreach (var rule in recurrence.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _ = new RecurrenceRule(rule);
        }
    }

    public RecurrenceCompletion CompleteOccurrence(TodoItem task, DateTimeOffset completedAt)
    {
        if (string.IsNullOrWhiteSpace(task.RawICalendar) || string.IsNullOrWhiteSpace(task.RecurrenceRule))
        {
            throw new InvalidOperationException("Task is not a recurring CalDAV task.");
        }

        var calendar = IcalCalendar.Load(task.RawICalendar) ?? throw new FormatException("Unable to parse recurring task.");
        var master = calendar.Todos.FirstOrDefault(candidate => candidate.RecurrenceIdentifier is null)
                     ?? throw new FormatException("Recurring task has no master VTODO.");
        var occurrenceStart = ToCalDateTime(task.Start ?? task.Due, task.HasDueTime || task.Start is not null)
                              ?? master.DtStart
                              ?? master.Due
                              ?? throw new InvalidOperationException("Recurring task requires DTSTART or DUE.");

        var exception = calendar.Create<Todo>();
        exception.Uid = master.Uid;
        exception.RecurrenceIdentifier = new RecurrenceIdentifier(occurrenceStart, null);
        exception.Summary = master.Summary;
        exception.Description = master.Description;
        exception.DtStart = task.Start is null ? null : ToCalDateTime(task.Start, true);
        exception.Due = ToCalDateTime(task.Due, task.HasDueTime);
        exception.Priority = master.Priority;
        exception.Status = "COMPLETED";
        exception.PercentComplete = 100;
        exception.Completed = new CalDateTime(completedAt.UtcDateTime, true);
        exception.DtStamp = CalDateTime.UtcNow;
        exception.LastModified = CalDateTime.UtcNow;

        var raw = new CalendarSerializer(calendar).SerializeToString()
                  ?? throw new InvalidOperationException("Unable to serialize recurring task.");
        var next = FindNextOccurrence(calendar, occurrenceStart);
        if (next is null)
        {
            return new RecurrenceCompletion(raw, null);
        }

        var normalized = task.Clone();
        normalized.RawICalendar = raw;
        normalized.BaseICalendar = raw;
        normalized.CompletedAt = null;
        normalized.Start = master.DtStart is null ? null : FromCalDateTime(next, out _);
        normalized.Due = ShiftDue(master, next, out var hasDueTime);
        normalized.HasDueTime = hasDueTime;
        normalized.UpdatedAt = completedAt;
        return new RecurrenceCompletion(raw, normalized);
    }

    private static CalDateTime? FindNextOccurrence(IcalCalendar calendar, CalDateTime current)
    {
        return calendar
            .GetOccurrences<Todo>(current, new EvaluationOptions { MaxUnmatchedIncrementsLimit = 10000 })
            .Select(occurrence => occurrence.Period.StartTime)
            .FirstOrDefault(candidate => candidate > current);
    }

    private static DateTime? ShiftDue(Todo master, CalDateTime nextStart, out bool hasDueTime)
    {
        hasDueTime = master.Due?.HasTime == true;
        if (master.Due is null)
        {
            return FromCalDateTime(nextStart, out hasDueTime);
        }

        var anchor = master.DtStart ?? master.Due;
        var shifted = nextStart.Add(master.Due.Subtract(anchor));
        return FromCalDateTime(shifted, out hasDueTime);
    }

    private static DateTime? FromCalDateTime(CalDateTime? value, out bool hasTime)
    {
        hasTime = value?.HasTime == true;
        if (value is null) return null;
        if (!value.HasTime) return value.Value.Date;
        if (value.IsUtc || !string.IsNullOrWhiteSpace(value.TzId)) return value.AsUtc.ToLocalTime();
        return DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
    }

    private static CalDateTime? ToCalDateTime(DateTime? value, bool hasTime)
    {
        if (value is null) return null;
        if (!hasTime) return new CalDateTime(DateOnly.FromDateTime(value.Value));
        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Local);
        return new CalDateTime(local.ToUniversalTime(), true);
    }

    private static TodoPriority FromRfcPriority(int priority) => priority switch
    {
        >= 1 and <= 3 => TodoPriority.High,
        >= 4 and <= 6 => TodoPriority.Medium,
        >= 7 and <= 9 => TodoPriority.Low,
        _ => TodoPriority.None
    };

    private static int ToRfcPriority(TodoPriority priority) => priority switch
    {
        TodoPriority.High => 1,
        TodoPriority.Medium => 5,
        TodoPriority.Low => 9,
        _ => 0
    };
}

public sealed record RecurrenceCompletion(string RawICalendar, TodoItem? NextOccurrence);
