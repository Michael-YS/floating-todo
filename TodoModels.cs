using System;
using System.Text.Json.Serialization;

namespace FloatingTodo;

public enum TodoPriority
{
    None = 0,
    Low = 3,
    Medium = 2,
    High = 1
}

public sealed class TodoListInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Color { get; init; }
    public string? AccountId { get; init; }
    public string? RemoteUrl { get; init; }
    public bool IsLocal { get; init; }

    public override string ToString() => Name;
}

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Uid { get; set; } = Guid.NewGuid().ToString("D");
    public string ListId { get; set; } = "personal";
    public DateTime? Start { get; set; }
    public DateTime? Due { get; set; }
    public bool HasDueTime { get; set; }
    public string Description { get; set; } = string.Empty;
    public TodoPriority Priority { get; set; }
    public string RecurrenceRule { get; set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? RemoteHref { get; set; }
    public string? ETag { get; set; }
    public string RawICalendar { get; set; } = string.Empty;
    public string BaseICalendar { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public bool IsDeleted { get; set; }
    public bool HasConflict { get; set; }
    public Guid? ParentTaskId { get; set; }
    public bool IsCompletionShadow { get; set; }

    [JsonIgnore]
    public bool IsCompleted => CompletedAt is not null;

    public TodoItem Clone() => new()
    {
        Id = Id,
        Title = Title,
        Uid = Uid,
        ListId = ListId,
        Start = Start,
        Due = Due,
        HasDueTime = HasDueTime,
        Description = Description,
        Priority = Priority,
        RecurrenceRule = RecurrenceRule,
        CompletedAt = CompletedAt,
        UpdatedAt = UpdatedAt,
        RemoteHref = RemoteHref,
        ETag = ETag,
        RawICalendar = RawICalendar,
        BaseICalendar = BaseICalendar,
        IsDirty = IsDirty,
        IsDeleted = IsDeleted,
        HasConflict = HasConflict,
        ParentTaskId = ParentTaskId,
        IsCompletionShadow = IsCompletionShadow
    };

    public void ApplyFrom(TodoItem other)
    {
        Title = other.Title;
        Uid = other.Uid;
        ListId = other.ListId;
        Start = other.Start;
        Due = other.Due;
        HasDueTime = other.HasDueTime;
        Description = other.Description;
        Priority = other.Priority;
        RecurrenceRule = other.RecurrenceRule;
        CompletedAt = other.CompletedAt;
        UpdatedAt = DateTimeOffset.Now;
        RemoteHref = other.RemoteHref;
        ETag = other.ETag;
        RawICalendar = other.RawICalendar;
        BaseICalendar = other.BaseICalendar;
        IsDirty = other.IsDirty;
        IsDeleted = other.IsDeleted;
        HasConflict = other.HasConflict;
        ParentTaskId = other.ParentTaskId;
        IsCompletionShadow = other.IsCompletionShadow;
    }
}
