using System;

namespace FloatingTodo;

public sealed record AccountRecord(
    string Id,
    string Name,
    string BaseUrl,
    string Username,
    bool Enabled);

public sealed record CalendarRecord(
    string Id,
    string AccountId,
    string RemoteUrl,
    string DisplayName,
    string Color,
    bool Enabled,
    string? CTag,
    string? SyncToken);

public enum OutboxOperation
{
    Upsert,
    Delete
}

public sealed record PendingChange(
    Guid TaskId,
    OutboxOperation Operation,
    DateTimeOffset QueuedAt,
    TodoItem Task,
    CalendarRecord Calendar,
    AccountRecord Account);

public sealed record StoredConflict(
    Guid TaskId,
    string CalendarId,
    string BaseICalendar,
    string LocalICalendar,
    string ServerICalendar,
    DateTimeOffset DetectedAt);

