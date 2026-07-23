using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FloatingTodo;

public partial class SettingsWindow : Window
{
    private static readonly string[] Palette =
    [
        "#55C9B0", "#6FA8FF", "#F3AD55", "#B38AF3",
        "#FF817C", "#58C7E8", "#D7C65A", "#E98EB7"
    ];

    private readonly AppDatabase _database;
    private readonly CredentialVault _vault;
    private readonly CalDavClient _client;
    private readonly TaskStore _store;
    private readonly DispatcherTimer _connectionStatusTimer;
    private bool _refreshingStartup;

    public SettingsWindow(AppDatabase database, CredentialVault vault, CalDavClient client, TaskStore store)
    {
        InitializeComponent();
        _database = database;
        _vault = vault;
        _client = client;
        _store = store;
        _connectionStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _connectionStatusTimer.Tick += (_, _) =>
        {
            _connectionStatusTimer.Stop();
            ConnectionStatusText.Text = string.Empty;
        };
        Closed += (_, _) => _connectionStatusTimer.Stop();
        RefreshStartupSetting();
        RefreshPanels();
    }

    public event Action? ConfigurationChanged;

    private void RefreshStartupSetting()
    {
        _refreshingStartup = true;
        try
        {
            StartWithWindowsCheckBox.IsChecked = StartupManager.IsEnabled();
            StartupStatusText.Text = StartWithWindowsCheckBox.IsChecked == true
                ? "已开启；下次登录 Windows 时自动运行。"
                : "使用当前用户启动项，不需要管理员权限。";
            StartupStatusText.Foreground = (Brush)FindResource("SecondaryText");
        }
        catch (Exception exception)
        {
            StartWithWindowsCheckBox.IsEnabled = false;
            StartupStatusText.Text = exception.Message;
            StartupStatusText.Foreground = BrushFromHex("#FF8A86");
        }
        finally
        {
            _refreshingStartup = false;
        }
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingStartup)
        {
            return;
        }

        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            StartupManager.SetEnabled(enabled);
            StartupStatusText.Text = enabled
                ? "已开启；下次登录 Windows 时自动运行。"
                : "已关闭开机启动。";
            StartupStatusText.Foreground = (Brush)FindResource("SecondaryText");
        }
        catch (Exception exception)
        {
            _refreshingStartup = true;
            StartWithWindowsCheckBox.IsChecked = !enabled;
            _refreshingStartup = false;
            StartupStatusText.Text = exception.Message;
            StartupStatusText.Foreground = BrushFromHex("#FF8A86");
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var name = AccountNameBox.Text.Trim();
        var url = UrlBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (name.Length == 0 || url.Length == 0 || username.Length == 0 || password.Length == 0)
        {
            ShowStatus("请填写名称、URL、用户名和密码。", true);
            return;
        }

        ConnectButton.IsEnabled = false;
        ShowStatus("正在连接并发现 VTODO 清单…", false);
        try
        {
            var accountId = Guid.NewGuid().ToString("D");
            var account = new AccountRecord(accountId, name, url, username, true);
            var discovered = await _client.DiscoverAsync(account, password);
            if (discovered.Count == 0)
            {
                ShowStatus("连接成功，但没有发现支持 VTODO 的任务清单。", true);
                return;
            }

            _database.UpsertAccount(account);
            _vault.WritePassword(accountId, username, password);
            var existing = _database.GetCalendars(accountId).ToDictionary(calendar => calendar.RemoteUrl, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < discovered.Count; index++)
            {
                var remote = discovered[index];
                if (existing.TryGetValue(remote.RemoteUrl, out var known))
                {
                    _database.UpsertCalendar(known with
                    {
                        DisplayName = remote.DisplayName,
                        CTag = remote.CTag,
                        SyncToken = remote.SyncToken
                    });
                    continue;
                }

                _database.UpsertCalendar(new CalendarRecord(
                    Guid.NewGuid().ToString("D"), accountId, remote.RemoteUrl, remote.DisplayName,
                    Palette[index % Palette.Length], true, remote.CTag, remote.SyncToken));
            }

            var removedPlaceholders = _database.DeletePlaceholderTasks();
            _database.SetCalendarsEnabledForAccount("local", false);
            _store.Reload();
            PasswordBox.Clear();
            var cleanupStatus = removedPlaceholders > 0 ? $"，已移除 {removedPlaceholders} 个示例任务" : string.Empty;
            ShowStatus($"已发现 {discovered.Count} 个任务清单{cleanupStatus}。", false);
            RefreshPanels();
            ConfigurationChanged?.Invoke();
        }
        catch (Exception exception)
        {
            AppLogger.Error(
                "CalDAV account discovery failed.",
                exception,
                ("account", name),
                ("serverUrl", url));
            ShowStatus(exception.Message, true);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void RefreshPanels()
    {
        AccountsPanel.Children.Clear();
        foreach (var account in _database.GetAccounts().Where(account => account.Id != "local"))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = account.Name, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = $"{account.Username} · {account.BaseUrl}",
                Foreground = (Brush)FindResource("SecondaryText"),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(text);
            var delete = new Button
            {
                Content = "删除",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF8A86")),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 5, 10, 5),
                Tag = account
            };
            delete.Click += DeleteAccount_Click;
            Grid.SetColumn(delete, 1);
            row.Children.Add(delete);
            AccountsPanel.Children.Add(row);
        }

        if (AccountsPanel.Children.Count == 0)
        {
            AccountsPanel.Children.Add(new TextBlock
            {
                Text = "尚未添加 CalDAV 账号",
                Foreground = (Brush)FindResource("SecondaryText")
            });
        }

        CalendarsPanel.Children.Clear();
        foreach (var calendar in _database.GetCalendars().Where(calendar => calendar.AccountId != "local"))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var colorButton = new Button
            {
                Width = 7,
                Height = 30,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = BrushFromHex(calendar.Color),
                Tag = calendar,
                ToolTip = "切换颜色"
            };
            colorButton.Click += CalendarColor_Click;
            row.Children.Add(colorButton);

            var enabled = new CheckBox
            {
                Content = calendar.DisplayName,
                IsChecked = calendar.Enabled,
                Foreground = (Brush)FindResource("PrimaryText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Tag = calendar
            };
            enabled.Checked += CalendarEnabled_Changed;
            enabled.Unchecked += CalendarEnabled_Changed;
            Grid.SetColumn(enabled, 1);
            row.Children.Add(enabled);

            var accountName = _database.GetAccounts().FirstOrDefault(account => account.Id == calendar.AccountId)?.Name ?? string.Empty;
            var accountText = new TextBlock
            {
                Text = accountName,
                Foreground = (Brush)FindResource("SecondaryText"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(accountText, 2);
            row.Children.Add(accountText);
            CalendarsPanel.Children.Add(row);
        }
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountRecord account })
        {
            return;
        }

        var result = MessageBox.Show(
            $"删除账号“{account.Name}”及其本地缓存？服务器数据不会被删除。",
            "删除 CalDAV 账号",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _vault.DeletePassword(account.Id);
        _database.DeleteAccount(account.Id);
        if (_database.GetAccounts().All(candidate => candidate.Id == "local"))
        {
            _database.SetCalendarsEnabledForAccount("local", true);
        }
        _store.Reload();
        RefreshPanels();
        ConfigurationChanged?.Invoke();
    }

    private void CalendarEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: CalendarRecord calendar } checkBox)
        {
            return;
        }
        _database.UpdateCalendarPresentation(calendar.Id, checkBox.IsChecked == true, calendar.Color);
        _store.Reload();
        ConfigurationChanged?.Invoke();
    }

    private void CalendarColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CalendarRecord calendar } button)
        {
            return;
        }
        var index = Array.FindIndex(Palette, color => string.Equals(color, calendar.Color, StringComparison.OrdinalIgnoreCase));
        var color = Palette[(index + 1 + Palette.Length) % Palette.Length];
        _database.UpdateCalendarPresentation(calendar.Id, calendar.Enabled, color);
        button.Background = BrushFromHex(color);
        button.Tag = calendar with { Color = color };
        _store.Reload();
        ConfigurationChanged?.Invoke();
    }

    private void ShowStatus(string text, bool error)
    {
        _connectionStatusTimer.Stop();
        ConnectionStatusText.Text = text;
        ConnectionStatusText.Foreground = error ? BrushFromHex("#FF8A86") : (Brush)FindResource("SecondaryText");
        if (error)
        {
            _connectionStatusTimer.Start();
        }
    }

    private static Brush BrushFromHex(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
