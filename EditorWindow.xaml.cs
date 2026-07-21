using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace FloatingTodo;

public partial class EditorWindow : Window
{
    private readonly TodoItem _original;
    private readonly TodoItem _draft;
    private bool _discarded;
    private bool _deleted;
    private bool _deleteArmed;

    public EditorWindow(TodoItem item, IReadOnlyList<TodoListInfo> lists)
    {
        InitializeComponent();
        _original = item;
        _draft = item.Clone();
        ListBox.ItemsSource = lists;
        PriorityBox.ItemsSource = new[]
        {
            new PriorityChoice("无", TodoPriority.None),
            new PriorityChoice("低", TodoPriority.Low),
            new PriorityChoice("中", TodoPriority.Medium),
            new PriorityChoice("高", TodoPriority.High)
        };
        PriorityBox.DisplayMemberPath = nameof(PriorityChoice.Label);
        PriorityBox.SelectedValuePath = nameof(PriorityChoice.Value);

        TitleBox.Text = _draft.Title;
        ListBox.SelectedValue = _draft.ListId;
        StartDatePicker.SelectedDate = _draft.Start?.Date;
        StartTimeBox.Text = _draft.Start?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        DueDatePicker.SelectedDate = _draft.Due?.Date;
        DueTimeBox.Text = _draft.HasDueTime ? _draft.Due?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        PriorityBox.SelectedValue = _draft.Priority;
        DescriptionBox.Text = _draft.Description;
        RecurrenceBox.Text = _draft.RecurrenceRule;
    }

    public event Action<TodoItem>? TaskSaved;
    public event Action<Guid>? TaskDeleted;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        _discarded = true;
        Close();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            DeleteButton.Content = "再次点击确认删除";
            DeleteButton.Background = System.Windows.Media.Brushes.DarkRed;
            return;
        }

        _deleted = true;
        TaskDeleted?.Invoke(_original.Id);
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_discarded && !_deleted)
        {
            if (!TryBuildDraft(out var error))
            {
                ValidationText.Text = error;
                e.Cancel = true;
                return;
            }

            TaskSaved?.Invoke(_draft);
        }

        base.OnClosing(e);
    }

    private bool TryBuildDraft(out string error)
    {
        error = string.Empty;
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            error = "标题不能为空";
            return false;
        }

        if (!TryCombineDateAndTime(StartDatePicker.SelectedDate, StartTimeBox.Text, out var start, out _, out error))
        {
            return false;
        }

        if (!TryCombineDateAndTime(DueDatePicker.SelectedDate, DueTimeBox.Text, out var due, out var hasDueTime, out error))
        {
            return false;
        }

        if (start is not null && due is not null && start > due)
        {
            error = "开始时间不能晚于截止时间";
            return false;
        }

        _draft.Title = title;
        _draft.ListId = ListBox.SelectedValue as string ?? _draft.ListId;
        _draft.Start = start;
        _draft.Due = due;
        _draft.HasDueTime = hasDueTime;
        _draft.Priority = PriorityBox.SelectedValue is TodoPriority priority ? priority : TodoPriority.None;
        _draft.Description = DescriptionBox.Text;
        _draft.RecurrenceRule = RecurrenceBox.Text.Trim();
        return true;
    }

    private static bool TryCombineDateAndTime(DateTime? date, string input, out DateTime? result, out bool hasTime, out string error)
    {
        result = date?.Date;
        hasTime = false;
        error = string.Empty;
        input = input.Trim();
        if (input.Length == 0)
        {
            return true;
        }

        if (date is null)
        {
            error = "填写时间前请先选择日期";
            return false;
        }

        if (!TimeSpan.TryParseExact(input, ["h\\:mm", "hh\\:mm", "H\\:mm", "HH\\:mm"], CultureInfo.InvariantCulture, out var time))
        {
            error = "时间格式应为 14:30";
            return false;
        }

        result = date.Value.Date.Add(time);
        hasTime = true;
        return true;
    }

    private sealed record PriorityChoice(string Label, TodoPriority Value);
}
