using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace FloatingTodo;

public partial class EditorWindow : Window
{
    private const string CustomRecurrence = "__CUSTOM__";
    private const string VisualRecurrence = "__VISUAL__";
    private readonly TodoItem _original;
    private readonly TodoItem _draft;
    private readonly EditorValues _initialValues;
    private bool _discarded;
    private bool _deleted;
    private bool _deleteArmed;
    private bool _restoringRecurrenceMode;

    public EditorWindow(TodoItem item, IReadOnlyList<TodoListInfo> lists)
    {
        InitializeComponent();
        RecurrencePresetBox.DisplayMemberPath = nameof(RecurrenceChoice.Label);
        RecurrencePresetBox.SelectedValuePath = nameof(RecurrenceChoice.Rule);
        RecurrencePresetBox.ItemsSource = RecurrenceChoices;
        FrequencyBox.DisplayMemberPath = nameof(RecurrenceChoice.Label);
        FrequencyBox.SelectedValuePath = nameof(RecurrenceChoice.Rule);
        FrequencyBox.ItemsSource = new[] { new RecurrenceChoice("天", "DAILY"), new("周", "WEEKLY"), new("月", "MONTHLY"), new("年", "YEARLY") };
        FrequencyBox.SelectedValue = "WEEKLY";
        MonthModeBox.ItemsSource = new[] { "沿用起始日", "指定日期（1,15,-1）", "第几个星期几" };
        MonthModeBox.SelectedIndex = 0;
        EndModeBox.ItemsSource = new[] { "永不结束", "达到总次数", "截至日期（含当天）" };
        EndModeBox.SelectedIndex = 0;
        PositionBox.ItemsSource = new[] { "第一个", "第二个", "第三个", "第四个", "第五个", "最后一个" };
        PositionBox.SelectedIndex = 0;
        var weekdayLabels = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        MonthWeekdayBox.ItemsSource = weekdayLabels;
        MonthWeekdayBox.SelectedIndex = 0;
        for (var i = 0; i < weekdayLabels.Length; i++)
            WeekdayPanel.Children.Add(new CheckBox { Content = weekdayLabels[i], Tag = RecurrenceConfiguration.Weekdays[i],
                Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText"), Margin = new Thickness(0, 0, 12, 6) });
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
        if (RecurrenceChoices.Exists(choice => choice.Rule == _draft.RecurrenceRule))
            RecurrencePresetBox.SelectedValue = _draft.RecurrenceRule;
        else if (RecurrenceConfiguration.TryParse(_draft.RecurrenceRule, _draft.Start is not null || _draft.HasDueTime, out var configuration))
        {
            LoadRecurrenceConfiguration(configuration);
            RecurrencePresetBox.SelectedValue = VisualRecurrence;
        }
        else RecurrencePresetBox.SelectedValue = CustomRecurrence;
        _initialValues = ReadEditorValues();
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

    private void RecurrencePresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecurrencePresetBox.SelectedValue is not string rule) return;
        RecurrencePanel.Visibility = rule == VisualRecurrence ? Visibility.Visible : Visibility.Collapsed;
        RecurrenceBox.IsReadOnly = rule != CustomRecurrence;
        RecurrenceBox.Visibility = rule == VisualRecurrence ? Visibility.Collapsed : Visibility.Visible;
        if (rule == VisualRecurrence && !_restoringRecurrenceMode)
        {
            if (RecurrenceConfiguration.TryParse(RecurrenceBox.Text, StartDatePicker.SelectedDate is not null || !string.IsNullOrWhiteSpace(DueTimeBox.Text), out var configuration))
                LoadRecurrenceConfiguration(configuration);
        }
        else if (rule == CustomRecurrence && e.RemovedItems.OfType<RecurrenceChoice>().Any(choice => choice.Rule == VisualRecurrence))
        {
            try { RecurrenceBox.Text = BuildConfiguredRecurrence(); }
            catch (FormatException ex)
            {
                ValidationText.Text = ex.Message;
                _restoringRecurrenceMode = true;
                RecurrencePresetBox.SelectedValue = VisualRecurrence;
                _restoringRecurrenceMode = false;
            }
        }
        else if (rule != CustomRecurrence && rule != VisualRecurrence)
        {
            RecurrenceBox.Text = rule;
        }
    }

    private void RecurrenceOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WeeklyPanel is null || MonthlyPanel is null || MonthWeekPanel is null || CountBox is null || UntilPicker is null) return;
        WeeklyPanel.Visibility = FrequencyBox.SelectedValue as string == "WEEKLY" ? Visibility.Visible : Visibility.Collapsed;
        MonthlyPanel.Visibility = FrequencyBox.SelectedValue as string == "MONTHLY" ? Visibility.Visible : Visibility.Collapsed;
        MonthDaysBox.Visibility = MonthModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        MonthWeekPanel.Visibility = MonthModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        CountBox.Visibility = EndModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        UntilPicker.Visibility = EndModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadRecurrenceConfiguration(RecurrenceConfiguration configuration)
    {
        FrequencyBox.SelectedValue = configuration.Frequency;
        IntervalBox.Text = configuration.Interval.ToString(CultureInfo.InvariantCulture);
        foreach (var box in WeekdayPanel.Children.OfType<CheckBox>())
            box.IsChecked = configuration.Days.Contains((string)box.Tag);
        MonthModeBox.SelectedIndex = configuration.Position is not null ? 2 : configuration.MonthDays.Length > 0 ? 1 : 0;
        MonthDaysBox.Text = configuration.MonthDays.Length > 0 ? configuration.MonthDays : "1";
        PositionBox.SelectedIndex = configuration.Position == -1 ? 5 : (configuration.Position ?? 1) - 1;
        MonthWeekdayBox.SelectedIndex = configuration.Days.Length == 1 ? Array.IndexOf(RecurrenceConfiguration.Weekdays, configuration.Days[0]) : 0;
        EndModeBox.SelectedIndex = configuration.Count is not null ? 1 : configuration.Until is not null ? 2 : 0;
        CountBox.Text = (configuration.Count ?? 10).ToString(CultureInfo.InvariantCulture);
        UntilPicker.SelectedDate = configuration.Until;
    }

    private string BuildConfiguredRecurrence()
    {
        if (!int.TryParse(IntervalBox.Text, out var interval) || interval < 1)
            throw new FormatException("重复间隔必须为正整数");
        var configuration = new RecurrenceConfiguration { Frequency = FrequencyBox.SelectedValue as string ?? "WEEKLY", Interval = interval };
        if (configuration.Frequency == "WEEKLY")
            configuration.Days = WeekdayPanel.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();
        if (configuration.Frequency == "MONTHLY")
        {
            if (MonthModeBox.SelectedIndex == 1)
            {
                if (string.IsNullOrWhiteSpace(MonthDaysBox.Text)) throw new FormatException("请填写每月日期");
                configuration.MonthDays = MonthDaysBox.Text;
            }
            if (MonthModeBox.SelectedIndex == 2)
            {
                configuration.Days = [RecurrenceConfiguration.Weekdays[MonthWeekdayBox.SelectedIndex]];
                configuration.Position = PositionBox.SelectedIndex == 5 ? -1 : PositionBox.SelectedIndex + 1;
            }
        }
        if (EndModeBox.SelectedIndex == 1)
        {
            if (!int.TryParse(CountBox.Text, out var count) || count < 1) throw new FormatException("重复次数必须为正整数");
            configuration.Count = count;
        }
        if (EndModeBox.SelectedIndex == 2)
            configuration.Until = UntilPicker.SelectedDate ?? throw new FormatException("请选择重复结束日期");
        return configuration.Build(StartDatePicker.SelectedDate is not null || !string.IsNullOrWhiteSpace(DueTimeBox.Text));
    }

    private void DueDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A time cannot exist without its date. In particular, clear the old
        // time when an existing task's due date is removed so closing the
        // editor does not reject the now-stale time value.
        if (DueDatePicker.SelectedDate is null)
        {
            DueTimeBox.Clear();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_discarded && !_deleted && ReadEditorValues() != _initialValues)
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

    private EditorValues ReadEditorValues() => new(
        TitleBox.Text.Trim(), ListBox.SelectedValue as string,
        StartDatePicker.SelectedDate, StartTimeBox.Text.Trim(),
        DueDatePicker.SelectedDate, DueTimeBox.Text.Trim(),
        PriorityBox.SelectedValue is TodoPriority priority ? priority : TodoPriority.None,
        DescriptionBox.Text, ReadRecurrenceValue());

    private string ReadRecurrenceValue()
    {
        if (RecurrencePresetBox.SelectedValue as string != VisualRecurrence) return RecurrenceBox.Text.Trim();
        try { return BuildConfiguredRecurrence(); }
        catch (FormatException) { return "__INVALID__"; }
    }

    // Compare the values displayed at open, since the time inputs omit seconds
    // and the task's sync metadata may change while the editor is open.
    private sealed record EditorValues(
        string Title, string? ListId, DateTime? StartDate, string StartTime,
        DateTime? DueDate, string DueTime, TodoPriority Priority,
        string Description, string RecurrenceRule);

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
        try
        {
            _draft.RecurrenceRule = RecurrencePresetBox.SelectedValue as string == VisualRecurrence
                ? BuildConfiguredRecurrence() : RecurrenceBox.Text.Trim();
            new ICalendarCodec().ValidateRecurrence(_draft.RecurrenceRule);
            if (_draft.RecurrenceRule.Length > 0 && start is null && due is null)
            {
                error = "重复任务需要开始或截止日期";
                return false;
            }
            if (RecurrencePresetBox.SelectedValue as string == VisualRecurrence && EndModeBox.SelectedIndex == 2 &&
                UntilPicker.SelectedDate < (start ?? due)?.Date)
            {
                error = "重复结束日期不能早于起始日期";
                return false;
            }
        }
        catch (FormatException ex) { error = ex.Message; return false; }
        catch (Exception)
        {
            error = "重复规则格式无效";
            return false;
        }
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

    private sealed record PriorityChoice(string Label, TodoPriority Value)
    {
        public override string ToString() => Label;
    }

    private sealed record RecurrenceChoice(string Label, string Rule)
    {
        public override string ToString() => Label;
    }

    private static readonly List<RecurrenceChoice> RecurrenceChoices =
    [
        new("不重复", string.Empty),
        new("每天", "FREQ=DAILY"),
        new("每个工作日", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"),
        new("每周", "FREQ=WEEKLY"),
        new("每月", "FREQ=MONTHLY"),
        new("每年", "FREQ=YEARLY"),
        new("高级配置", VisualRecurrence),
        new("原始 RRULE", CustomRecurrence)
    ];
}
