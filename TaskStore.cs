using System;
using System.Collections.Generic;
using System.Linq;

namespace FloatingTodo;

public sealed class TaskStore
{
    private static readonly TimeSpan CompletionUndoWindow = TimeSpan.FromMinutes(5);
    private readonly AppDatabase _database;
    private readonly ICalendarCodec _codec;
    private List<TodoListInfo> _lists = [];
    private List<TodoItem> _items = [];
    private readonly Dictionary<Guid, TodoItem> _recurrenceUndo = [];

    public TaskStore(AppDatabase? database = null, ICalendarCodec? codec = null)
    {
        _database = database ?? new AppDatabase();
        _codec = codec ?? new ICalendarCodec();
        EnsurePrototypeData();
        Reload();
    }

    public IReadOnlyList<TodoListInfo> Lists => _lists;
    public List<TodoItem> Items => _items;
    public AppDatabase Database => _database;
    public ICalendarCodec Codec => _codec;

    public void Reload()
    {
        _lists = _database.GetCalendars(enabledOnly: true)
            .Select(calendar => new TodoListInfo
            {
                Id = calendar.Id,
                Name = calendar.DisplayName,
                Color = calendar.Color,
                AccountId = calendar.AccountId,
                RemoteUrl = calendar.RemoteUrl,
                IsLocal = calendar.AccountId == "local"
            })
            .ToList();
        _items = _database.LoadTasks().ToList();
    }

    public TodoListInfo GetList(string id) =>
        Lists.FirstOrDefault(list => list.Id == id) ?? Lists.First();

    public void Upsert(TodoItem item)
    {
        item.IsPlaceholder = false;
        var existing = _items.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (existing is null)
        {
            item.UpdatedAt = DateTimeOffset.Now;
            _items.Add(item);
        }
        else
        {
            existing.ApplyFrom(item);
            item = existing;
        }

        var list = GetList(item.ListId);
        var raw = _codec.Serialize(item);
        _database.SaveLocalTask(item, raw, queueForSync: !list.IsLocal);
    }

    public void Delete(Guid id)
    {
        var item = _items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null)
        {
            return;
        }

        var list = GetList(item.ListId);
        _database.MarkTaskDeleted(item, queueForSync: !list.IsLocal);
        _items.Remove(item);
    }

    public void ToggleCompleted(TodoItem item)
    {
        if (item.IsCompletionShadow && item.ParentTaskId is Guid parentId && _recurrenceUndo.Remove(item.Id, out var original))
        {
            var parent = _items.FirstOrDefault(candidate => candidate.Id == parentId);
            if (parent is not null)
            {
                parent.ApplyFrom(original);
                Upsert(parent);
            }
            if (parentId != item.Id)
            {
                _items.Remove(item);
            }
            else
            {
                item.ParentTaskId = null;
                item.IsCompletionShadow = false;
            }
            return;
        }

        if (item.CompletedAt is null && !string.IsNullOrWhiteSpace(item.RecurrenceRule))
        {
            CompleteRecurringOccurrence(item);
            return;
        }

        item.CompletedAt = item.CompletedAt is null ? DateTimeOffset.Now : null;
        item.UpdatedAt = DateTimeOffset.Now;
        Upsert(item);
    }

    private void CompleteRecurringOccurrence(TodoItem item)
    {
        var completedAt = DateTimeOffset.Now;
        var original = item.Clone();
        var result = _codec.CompleteOccurrence(item, completedAt);
        var shadow = original.Clone();
        shadow.Id = Guid.NewGuid();
        shadow.ParentTaskId = item.Id;
        shadow.IsCompletionShadow = true;
        shadow.CompletedAt = completedAt;
        shadow.UpdatedAt = completedAt;
        _recurrenceUndo[shadow.Id] = original;

        if (result.NextOccurrence is null)
        {
            item.CompletedAt = completedAt;
            item.RawICalendar = result.RawICalendar;
        }
        else
        {
            item.ApplyFrom(result.NextOccurrence);
        }
        item.UpdatedAt = completedAt;
        var list = GetList(item.ListId);
        _database.SaveLocalTask(item, result.RawICalendar, queueForSync: !list.IsLocal);
        if (result.NextOccurrence is null)
        {
            item.ParentTaskId = item.Id;
            item.IsCompletionShadow = true;
            _recurrenceUndo[item.Id] = original;
        }
        else
        {
            _items.Add(shadow);
        }
    }

    public TimeSpan RemainingUndo(TodoItem item)
    {
        if (item.CompletedAt is null)
        {
            return TimeSpan.Zero;
        }

        var remaining = CompletionUndoWindow - (DateTimeOffset.Now - item.CompletedAt.Value);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public bool IsVisibleInHud(TodoItem item) =>
        !item.IsCompleted || RemainingUndo(item) > TimeSpan.Zero;

    private void EnsurePrototypeData()
    {
        if (_database.GetCalendars().Count > 0)
        {
            return;
        }

        _database.UpsertAccount(new AccountRecord("local", "本地演示", "local://prototype", string.Empty, false));
        foreach (var list in PrototypeLists())
        {
            _database.UpsertCalendar(new CalendarRecord(
                list.Id, "local", $"local://{list.Id}", list.Name, list.Color, true, null, null));
        }

        var today = DateTime.Today;
        var samples = new[]
        {
            new TodoItem { Title = "提交项目报告", ListId = "personal", Due = today.AddDays(-1).AddHours(17), HasDueTime = true, Priority = TodoPriority.High, IsPlaceholder = true },
            new TodoItem { Title = "回复邮件", ListId = "work", Due = today.AddHours(10), HasDueTime = true, Priority = TodoPriority.Medium, IsPlaceholder = true },
            new TodoItem { Title = "购买硬盘", ListId = "shopping", Due = today, IsPlaceholder = true },
            new TodoItem { Title = "预约体检", ListId = "health", Due = today.AddDays(1).AddHours(16).AddMinutes(30), HasDueTime = true, IsPlaceholder = true },
            new TodoItem { Title = "整理照片", ListId = "personal", Due = today.AddDays(3), IsPlaceholder = true },
            new TodoItem { Title = "检查服务器备份", ListId = "work", Due = today.AddDays(6), Priority = TodoPriority.Low, IsPlaceholder = true },
            new TodoItem { Title = "清理下载目录", ListId = "personal", IsPlaceholder = true }
        };
        foreach (var item in samples)
        {
            var raw = _codec.Serialize(item);
            _database.SaveLocalTask(item, raw, queueForSync: false);
        }
    }

    private static IReadOnlyList<TodoListInfo> PrototypeLists() =>
    [
        new() { Id = "personal", Name = "个人", Color = "#55C9B0", AccountId = "local", IsLocal = true },
        new() { Id = "work", Name = "工作", Color = "#6FA8FF", AccountId = "local", IsLocal = true },
        new() { Id = "shopping", Name = "购物", Color = "#F3AD55", AccountId = "local", IsLocal = true },
        new() { Id = "health", Name = "健康", Color = "#B38AF3", AccountId = "local", IsLocal = true }
    ];
}
