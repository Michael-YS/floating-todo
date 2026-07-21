using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatingTodo;

public partial class ConflictWindow : Window
{
    private readonly AppDatabase _database;
    private readonly ICalendarCodec _codec;
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private List<StoredConflict> _conflicts = [];
    private StoredConflict? _current;
    private TodoItem? _local;
    private TodoItem? _server;

    public ConflictWindow(AppDatabase database, ICalendarCodec codec)
    {
        InitializeComponent();
        _database = database;
        _codec = codec;
        LoadNext();
    }

    public event Action? ConflictResolved;

    private void LoadNext()
    {
        _conflicts = _database.GetConflicts().ToList();
        _current = _conflicts.FirstOrDefault();
        FieldsPanel.Children.Clear();
        _choices.Clear();
        if (_current is null)
        {
            Close();
            return;
        }

        var localRecord = _database.LoadTasks(includeDeleted: true).First(task => task.Id == _current.TaskId);
        _local = _codec.Parse(_current.LocalICalendar, _current.CalendarId, localRecord.RemoteHref ?? string.Empty, localRecord.ETag, localRecord.Id);
        _local.BaseICalendar = _current.ServerICalendar;
        _local.ETag = localRecord.ETag;
        _server = _codec.Parse(_current.ServerICalendar, _current.CalendarId, localRecord.RemoteHref ?? string.Empty, localRecord.ETag, localRecord.Id);
        CounterText.Text = $"{_conflicts.Count} 个冲突待处理 · {_local.Title}";

        AddField("title", "标题", _local.Title, _server.Title);
        AddField("description", "描述", _local.Description, _server.Description);
        AddField("start", "开始时间", FormatDate(_local.Start), FormatDate(_server.Start));
        AddField("due", "截止时间", FormatDate(_local.Due, _local.HasDueTime), FormatDate(_server.Due, _server.HasDueTime));
        AddField("priority", "优先级", PriorityText(_local.Priority), PriorityText(_server.Priority));
        AddField("recurrence", "重复规则", EmptyText(_local.RecurrenceRule), EmptyText(_server.RecurrenceRule));
        AddField("completed", "完成状态", _local.IsCompleted ? "已完成" : "未完成", _server.IsCompleted ? "已完成" : "未完成");
    }

    private void AddField(string key, string label, string local, string server)
    {
        var changed = !string.Equals(local, server, StringComparison.Ordinal);
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, 7),
            Background = changed ? BrushFromHex("#18FFB36A") : BrushFromHex("#0EFFFFFF")
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

        row.Children.Add(Cell(label, true));
        var localText = Cell(local, false);
        Grid.SetColumn(localText, 1);
        row.Children.Add(localText);
        var serverText = Cell(server, false);
        Grid.SetColumn(serverText, 2);
        row.Children.Add(serverText);

        var choice = new CheckBox
        {
            Content = changed ? "采用服务器" : "内容相同",
            IsEnabled = changed,
            Foreground = (Brush)FindResource("PrimaryText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 9, 4, 9)
        };
        Grid.SetColumn(choice, 3);
        row.Children.Add(choice);
        _choices[key] = choice;
        FieldsPanel.Children.Add(row);
    }

    private TextBlock Cell(string text, bool label) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = label ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = label ? (Brush)FindResource("PrimaryText") : (Brush)FindResource("SecondaryText"),
        Margin = new Thickness(10, 9, 8, 9),
        MaxHeight = 90
    };

    private void Resolve_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || _local is null || _server is null)
        {
            return;
        }

        var merged = _local.Clone();
        if (UseServer("title")) merged.Title = _server.Title;
        if (UseServer("description")) merged.Description = _server.Description;
        if (UseServer("start")) merged.Start = _server.Start;
        if (UseServer("due"))
        {
            merged.Due = _server.Due;
            merged.HasDueTime = _server.HasDueTime;
        }
        if (UseServer("priority")) merged.Priority = _server.Priority;
        if (UseServer("recurrence")) merged.RecurrenceRule = _server.RecurrenceRule;
        if (UseServer("completed")) merged.CompletedAt = _server.CompletedAt;

        merged.RawICalendar = _current.ServerICalendar;
        merged.BaseICalendar = _current.ServerICalendar;
        var raw = _codec.Serialize(merged);
        _database.ResolveConflict(merged, raw);
        ConflictResolved?.Invoke();
        LoadNext();
    }

    private bool UseServer(string key) => _choices.TryGetValue(key, out var choice) && choice.IsChecked == true;
    private static string FormatDate(DateTime? value, bool withTime = true) => value is null ? "无" : value.Value.ToString(withTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string EmptyText(string value) => string.IsNullOrWhiteSpace(value) ? "无" : value;
    private static string PriorityText(TodoPriority priority) => priority switch { TodoPriority.High => "高", TodoPriority.Medium => "中", TodoPriority.Low => "低", _ => "无" };
    private static Brush BrushFromHex(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

