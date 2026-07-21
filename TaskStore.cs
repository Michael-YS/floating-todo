using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FloatingTodo;

public sealed class TaskStore
{
    private static readonly TimeSpan CompletionUndoWindow = TimeSpan.FromMinutes(5);
    private readonly string _path;

    public TaskStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatingTodo");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "prototype-tasks.json");
        Lists = CreateLists();
        Items = LoadItems();
    }

    public IReadOnlyList<TodoListInfo> Lists { get; }
    public List<TodoItem> Items { get; }

    public TodoListInfo GetList(string id) =>
        Lists.FirstOrDefault(list => list.Id == id) ?? Lists[0];

    public void Upsert(TodoItem item)
    {
        var existing = Items.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (existing is null)
        {
            item.UpdatedAt = DateTimeOffset.Now;
            Items.Add(item);
        }
        else
        {
            existing.ApplyFrom(item);
        }

        Save();
    }

    public void Delete(Guid id)
    {
        Items.RemoveAll(item => item.Id == id);
        Save();
    }

    public void ToggleCompleted(TodoItem item)
    {
        item.CompletedAt = item.CompletedAt is null ? DateTimeOffset.Now : null;
        item.UpdatedAt = DateTimeOffset.Now;
        Save();
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

    private List<TodoItem> LoadItems()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(_path), JsonOptions()) ?? [];
            }
        }
        catch
        {
            // A malformed prototype cache must not prevent the HUD from opening.
        }

        return CreateSampleItems();
    }

    private void Save()
    {
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(Items, JsonOptions()));
        File.Move(tempPath, _path, true);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static IReadOnlyList<TodoListInfo> CreateLists() =>
    [
        new() { Id = "personal", Name = "个人", Color = "#55C9B0" },
        new() { Id = "work", Name = "工作", Color = "#6FA8FF" },
        new() { Id = "shopping", Name = "购物", Color = "#F3AD55" },
        new() { Id = "health", Name = "健康", Color = "#B38AF3" }
    ];

    private static List<TodoItem> CreateSampleItems()
    {
        var today = DateTime.Today;
        return
        [
            new() { Title = "提交项目报告", ListId = "work", Due = today.AddDays(-1).AddHours(17), HasDueTime = true, Priority = TodoPriority.High },
            new() { Title = "回复邮件", ListId = "work", Due = today.AddHours(10), HasDueTime = true, Priority = TodoPriority.Medium },
            new() { Title = "购买硬盘", ListId = "shopping", Due = today, Priority = TodoPriority.None },
            new() { Title = "预约体检", ListId = "health", Due = today.AddDays(1).AddHours(16).AddMinutes(30), HasDueTime = true },
            new() { Title = "整理照片", ListId = "personal", Due = today.AddDays(3) },
            new() { Title = "检查服务器备份", ListId = "work", Due = today.AddDays(6), Priority = TodoPriority.Low },
            new() { Title = "清理下载目录", ListId = "personal", Due = null }
        ];
    }
}
