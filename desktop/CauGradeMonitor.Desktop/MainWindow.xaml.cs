using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CauGradeMonitor.Desktop.Models;
using CauGradeMonitor.Desktop.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CauGradeMonitor.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly ProcessSupervisor _supervisor = new();
    private readonly List<string> _logLines = [];
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _supervisor.StatusChanged += status => Dispatcher.Invoke(() => RenderStatus(status));
        _supervisor.SnapshotChanged += snapshot => Dispatcher.Invoke(() => RenderSnapshot(snapshot));
        _supervisor.LogReceived += entry => Dispatcher.Invoke(() => AppendLog(entry));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsStore.Load();
        PopulateSettings(_settings);
        AppPaths.EnsureCreated();
        ActionStatusText.ToolTip = AppPaths.DataDirectory;
        SidebarVersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "1.1"}";
        ConfigureTrayIcon();
        AppendLog(new LogEntry(DateTimeOffset.Now, "系统", "桌面端已就绪。", ServicePhase.Stopped));
        var cachedSnapshot = SnapshotStore.Load();
        if (cachedSnapshot is not null)
        {
            RenderSnapshot(cachedSnapshot with { Source = "上次成功查询" });
            ActionStatusText.Text = "已恢复上次查询结果";
        }
        if (_settings.AutoStartServices) await StartServicesAsync();
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "CAU 成绩守望",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("启动全部", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await StartServicesAsync())));
        menu.Items.Add("停止全部", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await StopServicesAsync())));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ExitApplicationAsync())));
        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ExitApplicationAsync()
    {
        _allowClose = true;
        await _supervisor.StopAsync();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(2500, "CAU 成绩守望", "程序仍在后台运行。可双击托盘图标重新打开。", Forms.ToolTipIcon.Info);
            return;
        }

        _trayIcon?.Dispose();
        _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        ShowPage((sender as Button)?.Tag?.ToString() ?? "Dashboard");
    }

    private void ShowLogs_Click(object sender, RoutedEventArgs e) => ShowPage("Logs");

    private void ShowPage(string destination)
    {
        DashboardPanel.Visibility = destination == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = destination == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = destination == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        SetNavSelected(DashboardNav, destination == "Dashboard");
        SetNavSelected(SettingsNav, destination == "Settings");
        SetNavSelected(LogsNav, destination == "Logs");
        (PageTitleText.Text, PageSubtitleText.Text) = destination switch
        {
            "Settings" => ("设置", "配置 VPN、成绩查询、飞书通知与记忆选项"),
            "Logs" => ("运行日志", "查看 VPN 重连与成绩查询的完整过程"),
            _ => ("运行总览", "查看连接、成绩与通知状态")
        };
    }

    private static void SetNavSelected(System.Windows.Controls.Button button, bool selected)
    {
        button.Background = BrushFrom(selected ? "#2B4335" : "#00000000");
        button.Foreground = BrushFrom(selected ? "#FFFFFF" : "#C9D2CC");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartServicesAsync();

    private async Task StartServicesAsync()
    {
        if (_busy || _supervisor.IsRunning) return;
        try
        {
            SetBusy(true, "正在启动 VPN 与监控...");
            _settings = ReadSettingsFromForm();
            _settingsStore.Save(_settings);
            await _supervisor.StartAsync(_settings);
            ActionStatusText.Text = "运行中";
        }
        catch (Exception error)
        {
            ActionStatusText.Text = "启动失败";
            MessageBox.Show(this, error.Message, "无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowPage("Settings");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopServicesAsync();

    private async Task StopServicesAsync()
    {
        if (_busy || !_supervisor.IsRunning) return;
        try
        {
            SetBusy(true, "正在停止...");
            await _supervisor.StopAsync();
            ActionStatusText.Text = "已停止";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        StartButton.IsEnabled = !busy && !_supervisor.IsRunning;
        StopButton.IsEnabled = !busy && _supervisor.IsRunning;
        if (!string.IsNullOrWhiteSpace(message)) ActionStatusText.Text = message;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = ReadSettingsFromForm();
            _settingsStore.Save(_settings);
            SettingsStatusText.Text = _supervisor.IsRunning ? "设置已保存；停止并重新启动后生效。" : "设置已安全保存。";
            ActionStatusText.Text = "设置已保存";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TestFeishu_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromForm();
            if (string.IsNullOrWhiteSpace(settings.FeishuWebhook)) throw new InvalidOperationException("请先填写飞书机器人 Webhook。");
            SettingsStatusText.Text = "正在发送测试消息...";
            await _supervisor.TestFeishuAsync(settings);
            SettingsStatusText.Text = "飞书测试消息发送成功。";
        }
        catch (Exception error)
        {
            SettingsStatusText.Text = "飞书测试失败。";
            MessageBox.Show(this, error.Message, "飞书测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseEasierConnect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 EasierConnect.exe",
            Filter = "EasierConnect|EasierConnect.exe|Windows 程序|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) EasierPathText.Text = dialog.FileName;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDirectory);
    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogsDirectory);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
        RecentLogText.Clear();
        FullLogText.Clear();
    }

    private void PopulateSettings(AppSettings settings)
    {
        VpnServerText.Text = settings.VpnServer;
        VpnPortText.Text = settings.VpnPort.ToString();
        VpnUsernameText.Text = settings.VpnUsername;
        VpnPasswordInput.Password = settings.VpnPassword;
        SocksBindText.Text = settings.SocksBind;
        ResolveRuleText.Text = settings.ResolveRule;
        EasierPathText.Text = settings.EasierConnectPath;
        RestartMinutesText.Text = settings.VpnRestartMinutes.ToString();
        EofCountText.Text = settings.EofRestartCount.ToString();
        PollIntervalText.Text = settings.PollIntervalSeconds.ToString();
        LoginUrlText.Text = settings.LoginUrl;
        GradeUrlText.Text = settings.GradeUrl;
        ListUrlText.Text = settings.ListUrl;
        FeishuWebhookInput.Password = settings.FeishuWebhook;
        FeishuSecretInput.Password = settings.FeishuSecret;
        FeishuAtAllCheck.IsChecked = settings.FeishuAtAll;
        NotifyOnStartCheck.IsChecked = settings.NotifyOnStart;
        RememberSecretsCheck.IsChecked = settings.RememberSecrets;
        AutoStartCheck.IsChecked = settings.AutoStartServices;
        MinimizeToTrayCheck.IsChecked = settings.MinimizeToTray;
        NextCheckText.Text = $"查询间隔：{settings.PollIntervalSeconds} 秒";
    }

    private AppSettings ReadSettingsFromForm()
    {
        var settings = new AppSettings
        {
            RememberSecrets = RememberSecretsCheck.IsChecked == true,
            AutoStartServices = AutoStartCheck.IsChecked == true,
            MinimizeToTray = MinimizeToTrayCheck.IsChecked == true,
            VpnServer = Required(VpnServerText.Text, "VPN 服务器"),
            VpnPort = ParseInteger(VpnPortText.Text, "VPN 端口", 1, 65535),
            VpnUsername = VpnUsernameText.Text.Trim(),
            VpnPassword = VpnPasswordInput.Password,
            SocksBind = Required(SocksBindText.Text, "SOCKS5 地址"),
            ResolveRule = ResolveRuleText.Text.Trim(),
            EasierConnectPath = EasierPathText.Text.Trim(),
            VpnRestartMinutes = ParseInteger(RestartMinutesText.Text, "VPN 重连间隔", 0, 10080),
            EofRestartCount = ParseInteger(EofCountText.Text, "EOF 阈值", 0, 100),
            PollIntervalSeconds = ParseInteger(PollIntervalText.Text, "查询间隔", 30, 86400),
            LoginUrl = Required(LoginUrlText.Text, "统一身份认证入口"),
            GradeUrl = Required(GradeUrlText.Text, "成绩页面"),
            ListUrl = Required(ListUrlText.Text, "成绩查询接口"),
            FeishuWebhook = FeishuWebhookInput.Password.Trim(),
            FeishuSecret = FeishuSecretInput.Password.Trim(),
            FeishuAtAll = FeishuAtAllCheck.IsChecked == true,
            NotifyOnStart = NotifyOnStartCheck.IsChecked == true
        };
        NextCheckText.Text = $"查询间隔：{settings.PollIntervalSeconds} 秒";
        return settings;
    }

    private static int ParseInteger(string value, string fieldName, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException($"{fieldName}必须是 {minimum} 到 {maximum} 之间的整数。");
        }
        return parsed;
    }

    private static string Required(string value, string fieldName)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) throw new InvalidOperationException($"请填写{fieldName}。");
        return trimmed;
    }

    private void RenderStatus(RuntimeStatus status)
    {
        VpnHeaderDot.Fill = PhaseBrush(status.VpnPhase);
        MonitorHeaderDot.Fill = PhaseBrush(status.MonitorPhase);
        VpnBodyDot.Fill = PhaseBrush(status.VpnPhase);
        MonitorBodyDot.Fill = PhaseBrush(status.MonitorPhase);
        VpnHeaderText.Text = $"VPN {status.VpnText}";
        MonitorHeaderText.Text = $"监控{status.MonitorText}";
        VpnBodyText.Text = status.VpnText;
        MonitorBodyText.Text = status.MonitorText;
        VpnSinceText.Text = status.VpnConnectedAt.HasValue ? $"本次连接：{status.VpnConnectedAt.Value:HH:mm:ss}" : "--";
        StartButton.IsEnabled = !_busy && !_supervisor.IsRunning;
        StopButton.IsEnabled = !_busy && _supervisor.IsRunning;
    }

    private void RenderSnapshot(GradeSnapshot snapshot)
    {
        CoursesMetricText.Text = snapshot.Rows.ToString();
        GpaMetricText.Text = snapshot.Gpa;
        RequiredMetricText.Text = $"{snapshot.CountedRequired}/{snapshot.Required}";
        CreditsMetricText.Text = $"{snapshot.Credits:0.##} 学分";
        LastCheckMetricText.Text = snapshot.CheckedAt.ToString("HH:mm");
        SourceMetricText.Text = string.IsNullOrWhiteSpace(snapshot.Source) ? "查询完成" : $"来源：{snapshot.Source}";
        ActionStatusText.Text = "最近查询正常";
    }

    private void AppendLog(LogEntry entry)
    {
        var line = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Source}] {entry.Message}";
        _logLines.Add(line);
        if (_logLines.Count > 500) _logLines.RemoveRange(0, _logLines.Count - 500);
        FullLogText.Text = string.Join(Environment.NewLine, _logLines);
        FullLogText.ScrollToEnd();
        RecentLogText.Text = string.Join(Environment.NewLine, _logLines.TakeLast(12));
        RecentLogText.ScrollToEnd();
    }

    private static System.Windows.Media.Brush PhaseBrush(ServicePhase phase)
    {
        return BrushFrom(phase switch
        {
            ServicePhase.Running => "#247449",
            ServicePhase.Starting => "#C47A12",
            ServicePhase.Degraded => "#D09432",
            ServicePhase.Error => "#B44040",
            _ => "#98A19B"
        });
    }

    private static SolidColorBrush BrushFrom(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
