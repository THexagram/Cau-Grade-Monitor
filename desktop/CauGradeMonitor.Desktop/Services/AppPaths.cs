namespace CauGradeMonitor.Desktop.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CauGradeMonitor");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string MonitorConfigFile => Path.Combine(DataDirectory, "monitor-config.json");
    public static string MonitorStateFile => Path.Combine(DataDirectory, "grade-state.json");
    public static string MonitorGuiSnapshotFile => Path.Combine(DataDirectory, "monitor-gui-snapshot.json");
    public static string SnapshotFile => Path.Combine(DataDirectory, "last-snapshot.json");
    public static string BrowserProfileDirectory => Path.Combine(DataDirectory, "browser-profile");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public static string ResourceTelemetryFile => Path.Combine(LogsDirectory, $"resources-{DateTime.Now:yyyyMMdd}.csv");
    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BrowserProfileDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public static string? FindMonitorRoot()
    {
        var candidates = new List<string>
        {
            RuntimeDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i += 1, current = current.Parent)
        {
            candidates.Add(current.FullName);
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "monitor.js")));
    }

    public static string? FindNodeExecutable()
    {
        var bundled = Path.Combine(RuntimeDirectory, "node", "node.exe");
        if (File.Exists(bundled)) return bundled;

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return pathEntries
            .Select(entry => Path.Combine(entry, "node.exe"))
            .FirstOrDefault(File.Exists);
    }

    public static string? FindEasierConnect(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var candidates = new[]
        {
            Path.Combine(RuntimeDirectory, "EasierConnect.exe"),
            Path.Combine(AppContext.BaseDirectory, "EasierConnect.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
