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
    private readonly List<string> _availableGpaTypes = [];
    private readonly HashSet<string> _selectedGpaTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _includedCourseKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excludedCourseKeys = new(StringComparer.Ordinal);
    private List<GradeCourse> _latestCourses = [];
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _busy;
    private bool _gpaTypesConfigured;
    private bool _updatingGpaTypes;

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
        GradesPanel.Visibility = destination == "Grades" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = destination == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = destination == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        SetNavSelected(DashboardNav, destination == "Dashboard");
        SetNavSelected(GradesNav, destination == "Grades");
        SetNavSelected(SettingsNav, destination == "Settings");
        SetNavSelected(LogsNav, destination == "Logs");
        (PageTitleText.Text, PageSubtitleText.Text) = destination switch
        {
            "Grades" => ("全部成绩", "查看最近一次成功查询返回的课程与成绩"),
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
        _gpaTypesConfigured = settings.GpaSelectedTypesConfigured;
        _selectedGpaTypes.Clear();
        foreach (var type in settings.GpaSelectedTypes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(type)) _selectedGpaTypes.Add(type.Trim());
        }
        _includedCourseKeys.Clear();
        foreach (var key in settings.GpaIncludedCourseKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key)) _includedCourseKeys.Add(key.Trim());
        }
        _excludedCourseKeys.Clear();
        foreach (var key in settings.GpaExcludedCourseKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key)) _excludedCourseKeys.Add(key.Trim());
        }
        RefreshGpaTypeSelector();
        GpaScopeMetricLabel.Text = _gpaTypesConfigured
            ? SelectedTypesLabel(_selectedGpaTypes)
            : GpaScopeLabel(settings.GpaScope);
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
        var selectedTypesConfigured = _gpaTypesConfigured;
        _gpaTypesConfigured = selectedTypesConfigured;
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
            GpaScope = string.IsNullOrWhiteSpace(_settings.GpaScope) ? "required_and_sports" : _settings.GpaScope,
            GpaSelectedTypesConfigured = selectedTypesConfigured,
            GpaSelectedTypes = _selectedGpaTypes.Order(StringComparer.Ordinal).ToList(),
            GpaIncludedCourseKeys = _includedCourseKeys.Order(StringComparer.Ordinal).ToList(),
            GpaExcludedCourseKeys = _excludedCourseKeys.Order(StringComparer.Ordinal).ToList(),
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

    private void RefreshGpaTypeSelector()
    {
        _updatingGpaTypes = true;
        try
        {
            GpaTypesList.Children.Clear();
            var displayTypes = _availableGpaTypes
                .Concat(_selectedGpaTypes)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            foreach (var type in displayTypes)
            {
                var checkBox = new CheckBox
                {
                    Content = type,
                    Tag = type,
                    IsChecked = _selectedGpaTypes.Contains(type),
                    Margin = new Thickness(2, 4, 2, 4)
                };
                checkBox.Checked += GpaTypeSelectionChanged;
                checkBox.Unchecked += GpaTypeSelectionChanged;
                GpaTypesList.Children.Add(checkBox);
            }
        }
        finally
        {
            _updatingGpaTypes = false;
        }
        UpdateGpaTypesSummary();
    }

    private void GpaTypeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingGpaTypes || sender is not CheckBox { Tag: string type } checkBox) return;
        if (checkBox.IsChecked == true) _selectedGpaTypes.Add(type);
        else _selectedGpaTypes.Remove(type);
        _gpaTypesConfigured = true;
        UpdateGpaTypesSummary();
        RemoveRedundantCourseOverrides();
        RefreshGradesGrid();
    }

    private void SelectAllGpaTypes_Click(object sender, RoutedEventArgs e)
    {
        _selectedGpaTypes.Clear();
        foreach (var type in _availableGpaTypes) _selectedGpaTypes.Add(type);
        _gpaTypesConfigured = true;
        RefreshGpaTypeSelector();
        RemoveRedundantCourseOverrides();
        RefreshGradesGrid();
    }

    private void ClearGpaTypes_Click(object sender, RoutedEventArgs e)
    {
        _selectedGpaTypes.Clear();
        _gpaTypesConfigured = true;
        RefreshGpaTypeSelector();
        RemoveRedundantCourseOverrides();
        RefreshGradesGrid();
    }

    private void GradeIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not GradeCourse course ||
            !course.GpaEligible || string.IsNullOrWhiteSpace(course.Key)) return;

        var shouldInclude = checkBox.IsChecked == true;
        var baseIncluded = IsIncludedByCurrentTypeRule(course);
        _includedCourseKeys.Remove(course.Key);
        _excludedCourseKeys.Remove(course.Key);
        if (shouldInclude != baseIncluded)
        {
            if (shouldInclude) _includedCourseKeys.Add(course.Key);
            else _excludedCourseKeys.Add(course.Key);
        }

        RefreshGradesGrid();
        PersistGpaSelection();
        e.Handled = true;
    }

    private void RefreshGradesGrid()
    {
        GradesGrid.ItemsSource = _latestCourses
            .Select(course => course with { IncludedInGpa = IsEffectivelyIncluded(course) })
            .ToList();
    }

    private bool IsIncludedByCurrentTypeRule(GradeCourse course)
    {
        if (!course.GpaEligible) return false;
        return _gpaTypesConfigured ? _selectedGpaTypes.Contains(course.Type) : course.BaseIncludedInGpa;
    }

    private bool IsEffectivelyIncluded(GradeCourse course)
    {
        if (!course.GpaEligible) return false;
        if (_excludedCourseKeys.Contains(course.Key)) return false;
        if (_includedCourseKeys.Contains(course.Key)) return true;
        return IsIncludedByCurrentTypeRule(course);
    }

    private void RemoveRedundantCourseOverrides()
    {
        foreach (var course in _latestCourses.Where(course => !string.IsNullOrWhiteSpace(course.Key)))
        {
            var baseIncluded = IsIncludedByCurrentTypeRule(course);
            if (baseIncluded) _includedCourseKeys.Remove(course.Key);
            else _excludedCourseKeys.Remove(course.Key);
        }
    }

    private void PersistGpaSelection()
    {
        try
        {
            _settings.GpaSelectedTypesConfigured = _gpaTypesConfigured;
            _settings.GpaSelectedTypes = _selectedGpaTypes.Order(StringComparer.Ordinal).ToList();
            _settings.GpaIncludedCourseKeys = _includedCourseKeys.Order(StringComparer.Ordinal).ToList();
            _settings.GpaExcludedCourseKeys = _excludedCourseKeys.Order(StringComparer.Ordinal).ToList();
            _settingsStore.Save(_settings);
            var message = _supervisor.IsRunning
                ? "课程例外已保存；停止并重新启动后用于计算与通知。"
                : "课程例外已保存，下次启动时生效。";
            ActionStatusText.Text = message;
            SettingsStatusText.Text = message;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法保存绩点规则", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateGpaTypesSummary()
    {
        GpaTypesSummaryText.Text = _availableGpaTypes.Count == 0 && _selectedGpaTypes.Count == 0
            ? "成功查询后加载类型"
            : _selectedGpaTypes.Count == 0
                ? "未选择任何类型"
                : _selectedGpaTypes.Count <= 2
                    ? string.Join("、", _selectedGpaTypes.Order(StringComparer.Ordinal))
                    : $"已选择 {_selectedGpaTypes.Count} 个类型";
    }

    private void UpdateAvailableGpaTypes(IReadOnlyList<GradeCourse> courses, GradeSnapshot snapshot)
    {
        _availableGpaTypes.Clear();
        _availableGpaTypes.AddRange(courses
            .Select(course => course.Type)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

        if (!_gpaTypesConfigured && snapshot.GpaScope == "selected_types")
        {
            _selectedGpaTypes.Clear();
            foreach (var type in snapshot.SelectedTypes ?? []) _selectedGpaTypes.Add(type);
            _gpaTypesConfigured = true;
        }
        else if (!_gpaTypesConfigured && _selectedGpaTypes.Count == 0)
        {
            foreach (var type in courses.Where(course => course.BaseIncludedInGpa).Select(course => course.Type))
            {
                _selectedGpaTypes.Add(type);
            }
        }

        RefreshGpaTypeSelector();
    }

    private static string GpaScopeLabel(string scope) => scope switch
    {
        "required" => "必修课绩点",
        "all" => "全部可换算课程绩点",
        _ => "必修及体育绩点"
    };

    private static string SelectedTypesLabel(IEnumerable<string>? selectedTypes)
    {
        var types = selectedTypes?.Where(type => !string.IsNullOrWhiteSpace(type)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (types.Count == 0) return "未选择类型绩点";
        var joined = string.Join(" + ", types);
        return types.Count <= 2 && joined.Length <= 12 ? $"{joined}绩点" : $"已选 {types.Count} 类绩点";
    }

    private static string SnapshotGpaLabel(GradeSnapshot snapshot) => snapshot.GpaScope == "selected_types"
        ? SelectedTypesLabel(snapshot.SelectedTypes)
        : GpaScopeLabel(snapshot.GpaScope);

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
        var courses = snapshot.Courses ?? [];
        _latestCourses = courses.ToList();
        UpdateAvailableGpaTypes(courses, snapshot);
        CoursesMetricText.Text = snapshot.Rows.ToString();
        GpaMetricText.Text = snapshot.Gpa;
        GpaScopeMetricLabel.Text = SnapshotGpaLabel(snapshot);
        RequiredMetricText.Text = $"{snapshot.CountedRequired}/{snapshot.Required}";
        CreditsMetricText.Text = $"{snapshot.Credits:0.##} 学分";
        LastCheckMetricText.Text = snapshot.CheckedAt.ToString("HH:mm");
        SourceMetricText.Text = string.IsNullOrWhiteSpace(snapshot.Source) ? "查询完成" : $"来源：{snapshot.Source}";
        RefreshGradesGrid();
        GradesSummaryText.Text = courses.Count > 0
            ? $"共 {snapshot.Rows} 科 | 计入绩点 {snapshot.CountedRequired} 科 | {snapshot.Credits:0.##} 学分"
            : "当前缓存没有课程明细，成功查询后会自动显示";
        GradesUpdatedText.Text = $"{SnapshotGpaLabel(snapshot)} | {snapshot.CheckedAt:yyyy-MM-dd HH:mm:ss}";
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
