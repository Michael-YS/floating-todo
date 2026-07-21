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
}

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string ListId { get; set; } = "personal";
    public DateTime? Start { get; set; }
    public DateTime? Due { get; set; }
    public bool HasDueTime { get; set; }
    public string Description { get; set; } = string.Empty;
    public TodoPriority Priority { get; set; }
    public string RecurrenceRule { get; set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public bool IsCompleted => CompletedAt is not null;

    public TodoItem Clone() => new()
    {
        Id = Id,
        Title = Title,
        ListId = ListId,
        Start = Start,
        Due = Due,
        HasDueTime = HasDueTime,
        Description = Description,
        Priority = Priority,
        RecurrenceRule = RecurrenceRule,
        CompletedAt = CompletedAt,
        UpdatedAt = UpdatedAt
    };

    public void ApplyFrom(TodoItem other)
    {
        Title = other.Title;
        ListId = other.ListId;
        Start = other.Start;
        Due = other.Due;
        HasDueTime = other.HasDueTime;
        Description = other.Description;
        Priority = other.Priority;
        RecurrenceRule = other.RecurrenceRule;
        CompletedAt = other.CompletedAt;
        UpdatedAt = DateTimeOffset.Now;
    }
}
