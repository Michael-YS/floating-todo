using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace FloatingTodo;

public partial class MainWindow : Window
{
    private readonly AppDatabase _database;
    private readonly CredentialVault _vault;
    private readonly CalDavClient _calDavClient;
    private readonly ICalendarCodec _codec;
    private readonly TaskStore _store;
    private readonly CalDavSyncEngine _syncEngine;
    private readonly DispatcherTimer _temporaryTimer;
    private readonly DispatcherTimer _completionTimer;
    private readonly DispatcherTimer _scheduledSyncTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Dictionary<Guid, TextBlock> _completionCountdowns = new();
    private DateTimeOffset? _temporaryUntil;
    private bool _hovering;
    private bool _interactionOpen;
    private bool _isTemporary;
    private bool _allowExit;
    private int _newListIndex;
    private DateTime? _newDue = DateTime.Today;
    private DateTimeOffset _lastSyncAttempt = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        _database = new AppDatabase();
        _vault = new CredentialVault();
        _calDavClient = new CalDavClient();
        _codec = new ICalendarCodec();
        _store = new TaskStore(_database, _codec);
        _syncEngine = new CalDavSyncEngine(_database, _vault, _calDavClient, _codec);
        _syncEngine.StatusChanged += SyncEngine_StatusChanged;
        _syncEngine.DataChanged += SyncEngine_DataChanged;

        _temporaryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _temporaryTimer.Tick += TemporaryTimer_Tick;
        _completionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _completionTimer.Tick += CompletionTimer_Tick;
        _scheduledSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scheduledSyncTimer.Tick += ScheduledSyncTimer_Tick;
        _scheduledSyncTimer.Start();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "Floating Todo",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.MouseClick += TrayIcon_MouseClick;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        Loaded += MainWindow_Loaded;
        LocationChanged += (_, _) => NativeWindow.KeepInsideCurrentWorkingArea(this);
        Closing += MainWindow_Closing;
        RefreshTasks();
    }

    private static DrawingIcon LoadApplicationIcon()
    {
        var processPath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(processPath)
            ? DrawingSystemIcons.Application
            : DrawingIcon.ExtractAssociatedIcon(processPath) ?? DrawingSystemIcons.Application;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        NativeWindow.Configure(this);
        UpdateWorkingArea();
        NativeWindow.ResetToPrimaryTopRight(this);
        NativeWindow.SendToDesktop(this);
        if (_database.GetAccounts().Any(account => account.Enabled))
        {
            await SynchronizeAsync(SyncTrigger.Startup);
        }
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("临时显示", null, (_, _) => Dispatcher.Invoke(ShowTemporarily));
        menu.Items.Add("新建任务", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ShowTemporarily();
            OpenNewTask();
        }));
        menu.Items.Add("立即同步", null, (_, _) => Dispatcher.Invoke(async () =>
        {
            ShowTemporarily();
            await SynchronizeAsync(SyncTrigger.TrayRequest);
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("解决同步冲突", null, (_, _) => Dispatcher.Invoke(OpenConflicts));
        menu.Items.Add("重置 HUD 位置", null, (_, _) => Dispatcher.Invoke(() =>
        {
            NativeWindow.ResetToPrimaryTopRight(this);
            ShowTemporarily();
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        return menu;
    }

    private void TrayIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            Dispatcher.Invoke(ShowTemporarily);
        }
    }

    private void ShowTemporarily()
    {
        NativeWindow.ShowTemporarily(this);
        _isTemporary = true;
        ResetTemporaryCountdown();
    }

    private void ResetTemporaryCountdown()
    {
        _temporaryUntil = DateTimeOffset.Now.AddSeconds(15);
        _temporaryTimer.Start();
    }

    private void TemporaryTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isTemporary || _hovering || _interactionOpen || _temporaryUntil is null)
        {
            return;
        }

        if (DateTimeOffset.Now >= _temporaryUntil)
        {
            ReturnToDesktop();
        }
    }

    private void ReturnToDesktop()
    {
        _temporaryTimer.Stop();
        _temporaryUntil = null;
        _isTemporary = false;
        NativeWindow.SendToDesktop(this);
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _hovering = true;

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovering = false;
        if (_isTemporary)
        {
            ResetTemporaryCountdown();
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (NewTaskPanel.Visibility == Visibility.Visible)
        {
            CloseNewTask();
        }

        if (!_isTemporary && !_interactionOpen)
        {
            NativeWindow.SendToDesktop(this);
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (NewTaskPanel.Visibility == Visibility.Visible &&
            !NewTaskPanel.IsMouseOver &&
            !AddButton.IsMouseOver)
        {
            CloseNewTask();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (NewTaskPanel.Visibility == Visibility.Visible)
        {
            CloseNewTask();
        }
        else
        {
            OpenNewTask();
        }
    }

    private void OpenNewTask()
    {
        if (_store.Lists.Count == 0)
        {
            OpenSettings();
            return;
        }
        NewTaskPanel.Visibility = Visibility.Visible;
        NewTitleBox.Focus();
        _interactionOpen = true;
    }

    private void NewListButton_Click(object sender, RoutedEventArgs e)
    {
        _newListIndex = (_newListIndex + 1) % _store.Lists.Count;
        UpdateNewListButton();
    }

    private void UpdateNewListButton()
    {
        var list = _store.Lists[_newListIndex];
        NewListButton.Background = BrushFromHex(list.Color);
        NewListButton.ToolTip = $"{list.Name} · 单击切换清单";
    }

    private void DateButton_Click(object sender, RoutedEventArgs e)
    {
        DueCalendar.SelectedDate = _newDue;
        ManualDateBox.Text = _newDue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        DateErrorText.Text = string.Empty;
        DatePopup.IsOpen = true;
    }

    private void QuickDate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        _newDue = tag switch
        {
            "today" => DateTime.Today,
            "tomorrow" => DateTime.Today.AddDays(1),
            "saturday" => NextSaturday(DateTime.Today),
            "none" => null,
            _ => _newDue
        };
        UpdateDateButton();
        DatePopup.IsOpen = false;
    }

    private void DueCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DueCalendar.SelectedDate is not null)
        {
            _newDue = DueCalendar.SelectedDate.Value.Date;
            UpdateDateButton();
            DatePopup.IsOpen = false;
        }
    }

    private void ManualDate_Click(object sender, RoutedEventArgs e)
    {
        if (TryParseFriendlyDate(ManualDateBox.Text, out var date))
        {
            _newDue = date;
            UpdateDateButton();
            DatePopup.IsOpen = false;
            return;
        }

        DateErrorText.Text = "请输入 2026-07-25、7/25 或 7月25日";
    }

    private void UpdateDateButton()
    {
        DateButton.Content = _newDue switch
        {
            null => "无日期",
            var date when date.Value.Date == DateTime.Today => "今天",
            var date when date.Value.Date == DateTime.Today.AddDays(1) => "明天",
            var date => date.Value.ToString("M月d日")
        };
    }

    private void NewTitleBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseNewTask();
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        var title = NewTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            return;
        }

        _store.Upsert(new TodoItem
        {
            Title = title,
            ListId = _store.Lists[_newListIndex].Id,
            Due = _newDue,
            HasDueTime = false
        });
        NewTitleBox.Clear();
        CloseNewTask();
        RefreshTasks();
        _ = SynchronizeAsync(SyncTrigger.LocalChange);
    }

    private void CloseNewTask()
    {
        NewTitleBox.Clear();
        NewTaskPanel.Visibility = Visibility.Collapsed;
        DatePopup.IsOpen = false;
        _interactionOpen = false;
        if (_isTemporary)
        {
            ResetTemporaryCountdown();
        }
    }

    private void RefreshTasks()
    {
        GroupsPanel.Children.Clear();
        _completionCountdowns.Clear();
        var visibleItems = _store.Items
            .Where(_store.IsVisibleInHud)
            .OrderBy(GroupOrder)
            .ThenBy(PriorityOrder)
            .ThenBy(item => item.Due ?? DateTime.MaxValue)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToList();

        foreach (var group in visibleItems.GroupBy(GroupKey))
        {
            AddGroup(group.Key, group.ToList());
        }

        _completionTimer.IsEnabled = _completionCountdowns.Count > 0;
        Dispatcher.BeginInvoke(() =>
        {
            UpdateWorkingArea();
            NativeWindow.KeepInsideCurrentWorkingArea(this);
        }, DispatcherPriority.Loaded);
    }

    private void AddGroup(string title, IReadOnlyList<TodoItem> items)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = title == "逾期" ? BrushFromHex("#FF817C") : (System.Windows.Media.Brush)FindResource("SecondaryText"),
            Margin = new Thickness(2, GroupsPanel.Children.Count == 0 ? 2 : 14, 0, 5)
        };
        GroupsPanel.Children.Add(header);

        foreach (var item in items)
        {
            GroupsPanel.Children.Add(BuildTaskRow(item));
        }
    }

    private UIElement BuildTaskRow(TodoItem item)
    {
        var list = _store.GetList(item.ListId);
        var row = new Grid { MinHeight = 43, Opacity = item.IsCompleted ? 0.55 : 1 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(39) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var marker = new Border
        {
            Width = 3,
            Height = 27,
            Background = BrushFromHex(list.Color),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = list.Name
        };
        Grid.SetColumn(marker, 0);

        var complete = new Button
        {
            Content = item.IsCompleted ? "✓" : "",
            Width = 21,
            Height = 21,
            Padding = new Thickness(0),
            Background = item.IsCompleted ? BrushFromHex(list.Color) : Brushes.Transparent,
            BorderBrush = item.IsCompleted ? BrushFromHex(list.Color) : (System.Windows.Media.Brush)FindResource("SecondaryText"),
            BorderThickness = new Thickness(1),
            Foreground = Brushes.White,
            FontSize = 12,
            Cursor = Cursors.Hand,
            ToolTip = item.IsCompleted ? "撤销完成" : "标记完成"
        };
        complete.Template = RoundButtonTemplate();
        complete.Click += (_, _) =>
        {
            _store.ToggleCompleted(item);
            RefreshTasks();
            _ = SynchronizeAsync(SyncTrigger.LocalChange);
        };
        Grid.SetColumn(complete, 1);

        var title = new Button
        {
            Content = item.Title,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText"),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 14,
            Padding = new Thickness(1, 8, 6, 8),
            Cursor = Cursors.Hand,
            ToolTip = item.Title
        };
        if (item.IsCompleted)
        {
            title.ContentTemplate = CompletedTitleTemplate();
        }
        title.Click += (_, _) => OpenEditor(item);
        Grid.SetColumn(title, 2);

        var metadata = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item.Priority == TodoPriority.High && !item.IsCompleted)
        {
            metadata.Children.Add(new TextBlock
            {
                Text = "!",
                Foreground = BrushFromHex("#FF817C"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 6, 0)
            });
        }

        var metadataText = new TextBlock
        {
            Text = item.IsCompleted
                ? $"撤销 {FormatRemaining(_store.RemainingUndo(item))}"
                : item.HasDueTime && item.Due is not null ? item.Due.Value.ToString("HH:mm") : string.Empty,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryText"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        metadata.Children.Add(metadataText);
        if (item.IsCompleted)
        {
            _completionCountdowns[item.Id] = metadataText;
        }
        Grid.SetColumn(metadata, 3);

        row.Children.Add(marker);
        row.Children.Add(complete);
        row.Children.Add(title);
        row.Children.Add(metadata);
        return row;
    }

    private void OpenEditor(TodoItem item)
    {
        _interactionOpen = true;
        if (_isTemporary)
        {
            _temporaryTimer.Stop();
        }

        var editor = new EditorWindow(item, _store.Lists)
        {
            Owner = this,
            Topmost = _isTemporary
        };
        editor.TaskSaved += draft =>
        {
            _store.Upsert(draft);
            SyncText.Text = "等待同步";
            RefreshTasks();
            _ = SynchronizeAsync(SyncTrigger.LocalChange);
        };
        editor.TaskDeleted += id =>
        {
            _store.Delete(id);
            SyncText.Text = "等待同步";
            RefreshTasks();
            _ = SynchronizeAsync(SyncTrigger.LocalChange);
        };
        editor.ShowDialog();

        _interactionOpen = false;
        if (_isTemporary)
        {
            ResetTemporaryCountdown();
        }
    }

    private void CompletionTimer_Tick(object? sender, EventArgs e)
    {
        var expired = false;
        foreach (var item in _store.Items.Where(candidate => candidate.IsCompleted))
        {
            var remaining = _store.RemainingUndo(item);
            if (remaining <= TimeSpan.Zero)
            {
                expired = _completionCountdowns.ContainsKey(item.Id) || expired;
                continue;
            }

            if (_completionCountdowns.TryGetValue(item.Id, out var text))
            {
                text.Text = $"撤销 {FormatRemaining(remaining)}";
            }
        }

        if (expired)
        {
            RefreshTasks();
        }
        else if (_completionCountdowns.Count == 0)
        {
            _completionTimer.Stop();
        }
    }

    private string GroupKey(TodoItem item)
    {
        if (item.Due is null)
        {
            return "无日期";
        }

        var date = item.Due.Value.Date;
        if (date < DateTime.Today)
        {
            return "逾期";
        }

        if (date == DateTime.Today)
        {
            return "今天";
        }

        if (date == DateTime.Today.AddDays(1))
        {
            return "明天";
        }

        return date.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    }

    private static int GroupOrder(TodoItem item)
    {
        if (item.Due is null)
        {
            return int.MaxValue;
        }

        var offset = (item.Due.Value.Date - DateTime.Today).Days;
        return offset < 0 ? int.MinValue : offset;
    }

    private static int PriorityOrder(TodoItem item) => item.Priority switch
    {
        TodoPriority.High => 0,
        TodoPriority.Medium => 1,
        TodoPriority.Low => 2,
        _ => 3
    };

    private void UpdateWorkingArea()
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var workHeight = screen.WorkingArea.Height / Math.Max(1d, dpi);
        MaxHeight = Math.Max(260, workHeight - 36);
        TaskScroll.MaxHeight = Math.Max(120, MaxHeight - 100 - (NewTaskPanel.Visibility == Visibility.Visible ? 58 : 0));
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private async void ScheduledSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (!_database.GetAccounts().Any(account => account.Enabled))
        {
            return;
        }

        var pluggedIn = Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Online;
        var interval = pluggedIn ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5);
        if (DateTimeOffset.Now - _lastSyncAttempt >= interval)
        {
            await SynchronizeAsync(SyncTrigger.Scheduled);
        }
    }

    private async Task SynchronizeAsync(SyncTrigger trigger)
    {
        if (!_database.GetAccounts().Any(account => account.Enabled))
        {
            return;
        }

        _lastSyncAttempt = DateTimeOffset.Now;
        try
        {
            await _syncEngine.SynchronizeAsync(trigger);
        }
        catch
        {
            // StatusChanged already exposes the actionable error in the HUD.
        }
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            Dispatcher.InvokeAsync(() => SynchronizeAsync(SyncTrigger.NetworkRestored));
        }
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Dispatcher.InvokeAsync(() => SynchronizeAsync(SyncTrigger.Resume));
        }
    }

    private void SyncEngine_StatusChanged(SyncStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            SyncText.Text = status.State switch
            {
                SyncState.Synchronizing => "同步中",
                SyncState.WaitingToUpload => "等待同步",
                SyncState.Conflict => "有冲突",
                SyncState.Error => "同步错误",
                SyncState.Offline => "离线",
                _ => "刚刚同步"
            };
            SyncText.ToolTip = status.Message;
        });
    }

    private void SyncEngine_DataChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _store.Reload();
            _newListIndex = Math.Clamp(_newListIndex, 0, Math.Max(0, _store.Lists.Count - 1));
            if (_store.Lists.Count > 0)
            {
                UpdateNewListButton();
            }
            RefreshTasks();
        });
    }

    private void OpenSettings()
    {
        _interactionOpen = true;
        var settings = new SettingsWindow(_database, _vault, _calDavClient, _store)
        {
            Owner = this,
            Topmost = _isTemporary
        };
        settings.ConfigurationChanged += () =>
        {
            _store.Reload();
            _newListIndex = 0;
            if (_store.Lists.Count > 0)
            {
                UpdateNewListButton();
            }
            RefreshTasks();
            _ = SynchronizeAsync(SyncTrigger.LocalChange);
        };
        settings.ShowDialog();
        _interactionOpen = false;
        if (_isTemporary)
        {
            ResetTemporaryCountdown();
        }
    }

    private void OpenConflicts()
    {
        if (_database.GetConflicts().Count == 0)
        {
            System.Windows.MessageBox.Show("当前没有同步冲突。", "Floating Todo");
            return;
        }

        _interactionOpen = true;
        var window = new ConflictWindow(_database, _codec)
        {
            Owner = this,
            Topmost = _isTemporary
        };
        window.ConflictResolved += () =>
        {
            _store.Reload();
            RefreshTasks();
            _ = SynchronizeAsync(SyncTrigger.LocalChange);
        };
        window.ShowDialog();
        _interactionOpen = false;
        if (_isTemporary)
        {
            ResetTemporaryCountdown();
        }
    }

    private static DateTime NextSaturday(DateTime from)
    {
        var days = ((int)DayOfWeek.Saturday - (int)from.DayOfWeek + 7) % 7;
        return from.Date.AddDays(days);
    }

    private static bool TryParseFriendlyDate(string input, out DateTime date)
    {
        input = input.Trim();
        var formats = new[] { "yyyy-M-d", "yyyy/M/d", "M/d", "M月d日" };
        foreach (var format in formats)
        {
            if (!DateTime.TryParseExact(input, format, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                continue;
            }

            if (!format.Contains('y'))
            {
                parsed = new DateTime(DateTime.Today.Year, parsed.Month, parsed.Day);
                if (parsed.Date < DateTime.Today)
                {
                    parsed = parsed.AddYears(1);
                }
            }

            date = parsed.Date;
            return true;
        }

        date = default;
        return false;
    }

    private static System.Windows.Media.Brush BrushFromHex(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static string FormatRemaining(TimeSpan remaining) => $"{Math.Max(0, (int)remaining.TotalMinutes)}:{remaining.Seconds:00}";

    private static ControlTemplate RoundButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetBinding(Border.BackgroundProperty, new Binding(nameof(Button.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        factory.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Button.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        factory.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Button.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(content);
        template.VisualTree = factory;
        return template;
    }

    private static DataTemplate CompletedTitleTemplate()
    {
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextDecorationsProperty, TextDecorations.Strikethrough);
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.MaxHeightProperty, 42d);
        template.VisualTree = text;
        return template;
    }
}
