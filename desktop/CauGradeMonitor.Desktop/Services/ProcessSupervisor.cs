using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using CauGradeMonitor.Desktop.Models;

namespace CauGradeMonitor.Desktop.Services;

public sealed class ProcessSupervisor : IAsyncDisposable
{
    private const string GuiEventPrefix = "@@CAU_EVENT@@";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _logFileLock = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _supervisorTask;
    private Process? _vpnProcess;
    private Process? _monitorProcess;
    private AppSettings? _settings;
    private string? _monitorRoot;
    private string? _nodeExecutable;
    private string? _vpnExecutable;
    private DateTimeOffset? _vpnConnectedAt;
    private DateTimeOffset? _lastCheckAt;
    private TaskCompletionSource<bool>? _vpnReadySignal;
    private int _eofFailureCount;
    private int _vpnRestartRequested;
    private bool _stopping;

    public event Action<RuntimeStatus>? StatusChanged;
    public event Action<GradeSnapshot>? SnapshotChanged;
    public event Action<LogEntry>? LogReceived;

    public RuntimeStatus Status { get; private set; } = new(
        ServicePhase.Stopped,
        "未连接",
        ServicePhase.Stopped,
        "未运行",
        null,
        null);

    public bool IsRunning => _runCancellation is not null && !_runCancellation.IsCancellationRequested;

    public async Task StartAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) return;
            ValidateAndResolveRuntime(settings);
            _settings = settings;
            _stopping = false;
            _runCancellation = new CancellationTokenSource();
            UpdateStatus(ServicePhase.Starting, "正在连接", ServicePhase.Starting, "等待 VPN");

            try
            {
                await EnsureSocksPortAvailableAsync(settings, _runCancellation.Token);
                await StartVpnAsync(_runCancellation.Token);
                await StartMonitorAsync(_runCancellation.Token);
                _supervisorTask = SuperviseAsync(_runCancellation.Token);
            }
            catch
            {
                await StopProcessesAsync();
                _runCancellation.Dispose();
                _runCancellation = null;
                UpdateStatus(ServicePhase.Error, "启动失败", ServicePhase.Error, "启动失败");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_runCancellation is null) return;
            _stopping = true;
            _runCancellation.Cancel();
            await StopProcessesAsync();
            if (_supervisorTask is not null)
            {
                try
                {
                    await _supervisorTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the supervisor timer is cancelled.
                }
                catch (TimeoutException)
                {
                    WriteLog("系统", "监督线程停止超时，子进程已强制结束。", ServicePhase.Degraded);
                }
            }
            _supervisorTask = null;
            _runCancellation.Dispose();
            _runCancellation = null;
            _vpnConnectedAt = null;
            _lastCheckAt = null;
            UpdateStatus(ServicePhase.Stopped, "未连接", ServicePhase.Stopped, "未运行");
            WriteLog("系统", "VPN 与成绩监控已停止。", ServicePhase.Stopped);
        }
        finally
        {
            _stopping = false;
            _gate.Release();
        }
    }

    public async Task TestFeishuAsync(AppSettings settings)
    {
        ValidateMonitorRuntime();
        MonitorConfigWriter.Write(settings, _monitorRoot!);

        var startInfo = CreateMonitorStartInfo(settings);
        startInfo.ArgumentList.Add("--test-feishu");
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
        }
        WriteLog("飞书", "测试消息发送成功。", ServicePhase.Running);
    }

    private void ValidateAndResolveRuntime(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.VpnUsername)) throw new InvalidOperationException("请填写 VPN 用户名。");
        if (string.IsNullOrWhiteSpace(settings.VpnPassword)) throw new InvalidOperationException("请填写 VPN 密码。");
        if (settings.VpnPort is < 1 or > 65535) throw new InvalidOperationException("VPN 端口无效。");
        if (settings.PollIntervalSeconds < 30) throw new InvalidOperationException("查询间隔不能少于 30 秒。");
        if (settings.VpnRestartMinutes is > 0 and < 10) throw new InvalidOperationException("VPN 定时重连至少为 10 分钟，设为 0 可关闭。");

        _vpnExecutable = AppPaths.FindEasierConnect(settings.EasierConnectPath)
            ?? throw new FileNotFoundException("未找到 EasierConnect.exe，请在设置中选择程序路径。");
        ValidateMonitorRuntime();
        MonitorConfigWriter.Write(settings, _monitorRoot!);
    }

    private void ValidateMonitorRuntime()
    {
        _monitorRoot = AppPaths.FindMonitorRoot()
            ?? throw new FileNotFoundException("未找到 monitor.js。请使用完整发布包，或从项目根目录运行桌面端。");
        _nodeExecutable = AppPaths.FindNodeExecutable()
            ?? throw new FileNotFoundException("未找到 Node.js。请使用完整发布包，或安装 Node.js LTS。");
        if (!Directory.Exists(Path.Combine(_monitorRoot, "node_modules", "playwright-core")))
        {
            throw new DirectoryNotFoundException("未找到 playwright-core。请使用完整发布包，或先运行 npm install。");
        }
    }

    private async Task EnsureSocksPortAvailableAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var (host, port) = ParseHostPort(settings.SocksBind);
        if (!await CanConnectTcpAsync(host, port, TimeSpan.FromMilliseconds(500), cancellationToken)) return;

        var easierConnectProcesses = Process.GetProcessesByName("EasierConnect");
        foreach (var process in easierConnectProcesses)
        {
            try
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                // The final port check below reports whether takeover succeeded.
            }
            finally
            {
                process.Dispose();
            }
        }

        for (var attempt = 0; attempt < 20; attempt += 1)
        {
            if (!await CanConnectTcpAsync(host, port, TimeSpan.FromMilliseconds(300), cancellationToken)) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException($"SOCKS5 端口 {settings.SocksBind} 已被其他程序占用。");
    }

    private async Task StartVpnAsync(CancellationToken cancellationToken)
    {
        var settings = _settings!;
        Interlocked.Exchange(ref _eofFailureCount, 0);
        Interlocked.Exchange(ref _vpnRestartRequested, 0);
        var readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _vpnReadySignal = readySignal;

        var startInfo = new ProcessStartInfo
        {
            FileName = _vpnExecutable!,
            WorkingDirectory = Path.GetDirectoryName(_vpnExecutable!)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        AddArgument(startInfo, "-server", settings.VpnServer);
        AddArgument(startInfo, "-port", settings.VpnPort.ToString());
        AddArgument(startInfo, "-username", settings.VpnUsername);
        AddArgument(startInfo, "-password", settings.VpnPassword);
        AddArgument(startInfo, "-socks-bind", settings.SocksBind);
        if (!string.IsNullOrWhiteSpace(settings.ResolveRule)) AddArgument(startInfo, "-resolve", settings.ResolveRule);

        _vpnProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _vpnProcess.OutputDataReceived += (_, args) => HandleVpnOutput(args.Data);
        _vpnProcess.ErrorDataReceived += (_, args) => HandleVpnOutput(args.Data);
        if (!_vpnProcess.Start()) throw new InvalidOperationException("EasierConnect 启动失败。");
        _vpnProcess.BeginOutputReadLine();
        _vpnProcess.BeginErrorReadLine();
        WriteLog("VPN", "正在建立新会话。", ServicePhase.Starting);

        var deadline = DateTimeOffset.Now.AddSeconds(60);
        try
        {
            while (DateTimeOffset.Now < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_vpnProcess.HasExited) throw new InvalidOperationException($"EasierConnect 已退出，代码 {_vpnProcess.ExitCode}。");
                if (readySignal.Task.IsCompletedSuccessfully)
                {
                    _vpnConnectedAt = DateTimeOffset.Now;
                    UpdateStatus(ServicePhase.Running, "已连接", Status.MonitorPhase, Status.MonitorText);
                    WriteLog("VPN", $"SOCKS5 已就绪：{settings.SocksBind}", ServicePhase.Running);
                    return;
                }
                await Task.Delay(250, cancellationToken);
            }
            throw new TimeoutException("等待 VPN SOCKS5 服务启动超时。");
        }
        finally
        {
            if (ReferenceEquals(_vpnReadySignal, readySignal)) _vpnReadySignal = null;
        }
    }

    private async Task StartMonitorAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreateMonitorStartInfo(_settings!);
        _monitorProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _monitorProcess.OutputDataReceived += (_, args) => HandleMonitorOutput(args.Data, false);
        _monitorProcess.ErrorDataReceived += (_, args) => HandleMonitorOutput(args.Data, true);
        if (!_monitorProcess.Start()) throw new InvalidOperationException("成绩监控启动失败。");
        _monitorProcess.BeginOutputReadLine();
        _monitorProcess.BeginErrorReadLine();
        UpdateStatus(Status.VpnPhase, Status.VpnText, ServicePhase.Starting, "正在打开 Edge");
        WriteLog("监控", "成绩监控已启动，正在打开独立 Edge。", ServicePhase.Starting);
        await Task.Delay(200, cancellationToken);
    }

    private ProcessStartInfo CreateMonitorStartInfo(AppSettings settings)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _nodeExecutable!,
            WorkingDirectory = _monitorRoot!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(Path.Combine(_monitorRoot!, "monitor.js"));
        AddArgument(startInfo, "--config", AppPaths.MonitorConfigFile);
        startInfo.ArgumentList.Add("--no-prompt");
        startInfo.Environment["CAU_GUI_EVENTS"] = "1";
        startInfo.Environment["FEISHU_WEBHOOK_URL"] = settings.FeishuWebhook;
        startInfo.Environment["FEISHU_BOT_SECRET"] = settings.FeishuSecret;
        return startInfo;
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                if (_vpnProcess is null || _vpnProcess.HasExited)
                {
                    await RestartVpnAsync("VPN 进程已退出", cancellationToken);
                    continue;
                }

                if (Interlocked.Exchange(ref _vpnRestartRequested, 0) == 1)
                {
                    await RestartVpnAsync($"隧道连续出现 {Volatile.Read(ref _eofFailureCount)} 条 EOF", cancellationToken);
                    continue;
                }

                if (_settings!.VpnRestartMinutes > 0 && _vpnConnectedAt.HasValue &&
                    DateTimeOffset.Now >= _vpnConnectedAt.Value.AddMinutes(_settings.VpnRestartMinutes))
                {
                    await RestartVpnAsync($"达到 {_settings.VpnRestartMinutes} 分钟会话轮换时间", cancellationToken);
                    continue;
                }

                if (_monitorProcess is null || _monitorProcess.HasExited)
                {
                    WriteLog("监控", "监控进程已退出，正在自动重启。", ServicePhase.Degraded);
                    UpdateStatus(Status.VpnPhase, Status.VpnText, ServicePhase.Starting, "正在自动重启");
                    DisposeProcess(ref _monitorProcess);
                    await Task.Delay(2000, cancellationToken);
                    await StartMonitorAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                WriteLog("系统", $"自动恢复失败：{error.Message}", ServicePhase.Error);
                UpdateStatus(ServicePhase.Degraded, "正在重试", Status.MonitorPhase, Status.MonitorText);
            }
        }
    }

    private async Task RestartVpnAsync(string reason, CancellationToken cancellationToken)
    {
        WriteLog("VPN", $"正在重连：{reason}。成绩监控保持运行。", ServicePhase.Degraded);
        UpdateStatus(ServicePhase.Starting, "正在重连", Status.MonitorPhase, Status.MonitorText);
        await StopProcessAsync(_vpnProcess);
        DisposeProcess(ref _vpnProcess);
        await Task.Delay(1000, cancellationToken);
        await StartVpnAsync(cancellationToken);
        WriteLog("VPN", "VPN 重连完成。", ServicePhase.Running);
    }

    private void HandleVpnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || _stopping) return;
        if (line.Contains("SOCKS5 SERVER listening on", StringComparison.OrdinalIgnoreCase))
        {
            _vpnReadySignal?.TrySetResult(true);
        }
        if (line.Contains("Error occurred while", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("EOF", StringComparison.OrdinalIgnoreCase))
        {
            var count = Interlocked.Increment(ref _eofFailureCount);
            if (_settings is not null && _settings.EofRestartCount > 0 && count >= _settings.EofRestartCount)
            {
                Interlocked.Exchange(ref _vpnRestartRequested, 1);
            }
        }
        else if (line.Contains("handshake: read", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _eofFailureCount, 0);
        }

        if (line.Contains("Login success", StringComparison.OrdinalIgnoreCase))
        {
            _vpnConnectedAt = DateTimeOffset.Now;
            UpdateStatus(ServicePhase.Running, "已连接", Status.MonitorPhase, Status.MonitorText);
        }

        var sanitized = SanitizeVpnLine(line);
        if (sanitized is not null) WriteLog("VPN", sanitized, line.Contains("EOF") ? ServicePhase.Degraded : ServicePhase.Running);
    }

    private void HandleMonitorOutput(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line) || _stopping) return;
        if (line.StartsWith(GuiEventPrefix, StringComparison.Ordinal))
        {
            HandleMonitorEvent(line[GuiEventPrefix.Length..]);
            return;
        }
        WriteLog("监控", line, isError ? ServicePhase.Error : ServicePhase.Running);
    }

    private void HandleMonitorEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "browser_started":
                    UpdateStatus(Status.VpnPhase, Status.VpnText, ServicePhase.Starting, "等待登录或首次查询");
                    break;
                case "snapshot":
                    _lastCheckAt = DateTimeOffset.Now;
                    List<GradeCourse> courses;
                    List<string> selectedTypes = [];
                    if (root.TryGetProperty("courses", out var inlineCourses) && inlineCourses.ValueKind == JsonValueKind.Array)
                    {
                        courses = ReadCourses(inlineCourses);
                    }
                    else if (root.TryGetProperty("courseSnapshotWritten", out var snapshotWritten) && snapshotWritten.ValueKind == JsonValueKind.False)
                    {
                        courses = [];
                        WriteLog("系统", "课程明细快照写入失败，本次仅更新成绩汇总。", ServicePhase.Degraded);
                    }
                    else
                    {
                        var courseSnapshot = ReadCourseSnapshotFile();
                        courses = courseSnapshot.Courses;
                        selectedTypes = courseSnapshot.SelectedTypes;
                        var expectedCourseCount = root.TryGetProperty("courseCount", out var countElement) && countElement.TryGetInt32(out var count)
                            ? count
                            : root.GetProperty("rows").GetInt32();
                        if (courses.Count != expectedCourseCount)
                        {
                            WriteLog("系统", $"课程明细快照暂未就绪：期望 {expectedCourseCount} 科，读取到 {courses.Count} 科。", ServicePhase.Degraded);
                        }
                    }
                    var snapshot = new GradeSnapshot(
                        root.GetProperty("rows").GetInt32(),
                        root.GetProperty("gpa").GetString() ?? "-",
                        root.GetProperty("required").GetInt32(),
                        root.GetProperty("countedRequired").GetInt32(),
                        root.GetProperty("credits").GetDouble(),
                        root.GetProperty("source").GetString() ?? "",
                        _lastCheckAt.Value,
                        courses,
                        ReadJsonString(root, "gpaScope", "required_and_sports"),
                        selectedTypes);
                    SnapshotStore.Save(snapshot);
                    SnapshotChanged?.Invoke(snapshot);
                    UpdateStatus(Status.VpnPhase, Status.VpnText, ServicePhase.Running, "监测正常");
                    break;
                case "login_needed":
                    UpdateStatus(Status.VpnPhase, Status.VpnText, ServicePhase.Degraded, "等待登录或成绩页面");
                    break;
                case "grades_changed":
                    WriteLog("成绩", $"检测到 {root.GetProperty("count").GetInt32()} 项成绩变化，已发送通知。", ServicePhase.Running);
                    break;
                case "check_failed":
                    UpdateStatus(Status.VpnPhase, Status.VpnText, ServicePhase.Degraded, "查询暂时失败");
                    break;
                case "fatal":
                    UpdateStatus(Status.VpnPhase, Status.VpnText, ServicePhase.Error, "监控异常退出");
                    break;
            }
        }
        catch (Exception error)
        {
            WriteLog("系统", $"无法读取监控状态：{error.Message}", ServicePhase.Degraded);
        }
    }

    private static string ReadJsonString(JsonElement element, string propertyName, string fallback = "")
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static CourseSnapshotData ReadCourseSnapshotFile()
    {
        try
        {
            if (!File.Exists(AppPaths.MonitorGuiSnapshotFile)) return new([], []);
            using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.MonitorGuiSnapshotFile));
            var courses = document.RootElement.TryGetProperty("courses", out var courseElements) && courseElements.ValueKind == JsonValueKind.Array
                ? ReadCourses(courseElements)
                : [];
            var selectedTypes = document.RootElement.TryGetProperty("selectedTypes", out var typeElements) && typeElements.ValueKind == JsonValueKind.Array
                ? typeElements.EnumerateArray()
                    .Where(type => type.ValueKind == JsonValueKind.String)
                    .Select(type => type.GetString() ?? "")
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : [];
            return new(courses, selectedTypes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new([], []);
        }
    }

    private static List<GradeCourse> ReadCourses(JsonElement courseElements)
    {
        var courses = new List<GradeCourse>();
        foreach (var course in courseElements.EnumerateArray())
        {
            var term = ReadJsonString(course, "term");
            var code = ReadJsonString(course, "code");
            var name = ReadJsonString(course, "name");
            var credit = ReadJsonString(course, "credit");
            var includedInGpa = course.TryGetProperty("includedInGpa", out var included) && included.ValueKind == JsonValueKind.True;
            courses.Add(new GradeCourse(
                term,
                code,
                name,
                credit,
                ReadJsonString(course, "score"),
                ReadJsonString(course, "type"),
                includedInGpa,
                !course.TryGetProperty("gpaEligible", out var eligible) || eligible.ValueKind == JsonValueKind.True,
                ReadJsonString(course, "key", string.Join("||", term, code, name, credit)),
                course.TryGetProperty("baseIncludedInGpa", out var baseIncluded)
                    ? baseIncluded.ValueKind == JsonValueKind.True
                    : includedInGpa));
        }
        return courses;
    }

    private sealed record CourseSnapshotData(List<GradeCourse> Courses, List<string> SelectedTypes);

    private async Task StopProcessesAsync()
    {
        await StopProcessAsync(_monitorProcess);
        DisposeProcess(ref _monitorProcess);
        await StopProcessAsync(_vpnProcess);
        DisposeProcess(ref _vpnProcess);
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch
        {
            // Process may already be gone or inaccessible.
        }
    }

    private static void DisposeProcess(ref Process? process)
    {
        process?.Dispose();
        process = null;
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static (string Host, int Port) ParseHostPort(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"SOCKS5 地址无效：{value}");
        }
        return (value[..separator], port);
    }

    private static async Task<bool> CanConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? SanitizeVpnLine(string line)
    {
        if (line.Contains("client connection failed: could not read packet header", StringComparison.OrdinalIgnoreCase)) return null;
        if (line.Contains("client connection failed: from client to backend", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("aborted by the software in your host machine", StringComparison.OrdinalIgnoreCase)) return null;
        if (line.Contains("Password to encrypt", StringComparison.OrdinalIgnoreCase)) return "正在准备加密登录信息。";
        if (line.Contains("Encrypted Password", StringComparison.OrdinalIgnoreCase)) return "登录信息已加密。";
        if (line.Contains("RSA Key", StringComparison.OrdinalIgnoreCase)) return null;
        if (line.Length > 260) return null;
        if (line.StartsWith("000000", StringComparison.Ordinal) || line.All(character => Uri.IsHexDigit(character) || char.IsWhiteSpace(character))) return null;
        return line;
    }

    private void UpdateStatus(ServicePhase vpnPhase, string vpnText, ServicePhase monitorPhase, string monitorText)
    {
        Status = new RuntimeStatus(vpnPhase, vpnText, monitorPhase, monitorText, _vpnConnectedAt, _lastCheckAt);
        StatusChanged?.Invoke(Status);
    }

    private void WriteLog(string source, string message, ServicePhase level)
    {
        var entry = new LogEntry(DateTimeOffset.Now, source, message.Trim(), level);
        try
        {
            var path = Path.Combine(AppPaths.LogsDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (_logFileLock)
            {
                File.AppendAllText(path, $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{source}] {entry.Message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never stop monitoring.
        }
        LogReceived?.Invoke(entry);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
