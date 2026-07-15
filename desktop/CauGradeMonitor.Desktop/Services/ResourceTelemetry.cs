using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CauGradeMonitor.Desktop.Services;

internal static class ResourceTelemetry
{
    private const int TelemetryRetentionDays = 7;
    private const int AppLogRetentionDays = 14;
    private const double BytesPerMegabyte = 1024d * 1024d;
    private static readonly object FileLock = new();
    private static DateTime _lastCleanupDate = DateTime.MinValue;

    public static void Capture(string reason)
    {
        AppPaths.EnsureCreated();

        var gui = CaptureCurrentProcess();
        var vpn = CaptureNamedProcesses("EasierConnect");
        var node = CaptureNamedProcesses("node");
        var edge = CaptureNamedProcesses("msedge");
        var memory = ReadMemoryStatus();
        var values = new[]
        {
            DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            Csv(reason),
            Number(gui.Count), Megabytes(gui.WorkingSetBytes), Megabytes(gui.PrivateBytes), Number(gui.Handles), Number(gui.Threads),
            Number(vpn.Count), Megabytes(vpn.WorkingSetBytes), Megabytes(vpn.PrivateBytes), Number(vpn.Handles), Number(vpn.Threads),
            Number(node.Count), Megabytes(node.WorkingSetBytes), Megabytes(node.PrivateBytes), Number(node.Handles), Number(node.Threads),
            Number(edge.Count), Megabytes(edge.WorkingSetBytes), Megabytes(edge.PrivateBytes), Number(edge.Handles), Number(edge.Threads),
            memory.MemoryLoadPercent.ToString(CultureInfo.InvariantCulture),
            Megabytes(memory.AvailablePhysicalBytes),
            Megabytes(memory.AvailableCommitBytes)
        };

        lock (FileLock)
        {
            var telemetryFile = AppPaths.ResourceTelemetryFile;
            if (!File.Exists(telemetryFile))
            {
                File.AppendAllText(telemetryFile,
                    "Timestamp,Reason,GuiCount,GuiWorkingSetMB,GuiPrivateMB,GuiHandles,GuiThreads," +
                    "VpnCount,VpnWorkingSetMB,VpnPrivateMB,VpnHandles,VpnThreads," +
                    "NodeCount,NodeWorkingSetMB,NodePrivateMB,NodeHandles,NodeThreads," +
                    "EdgeCount,EdgeWorkingSetMB,EdgePrivateMB,EdgeHandles,EdgeThreads," +
                    "SystemMemoryLoadPercent,AvailablePhysicalMB,AvailableCommitMB" + Environment.NewLine);
            }
            File.AppendAllText(telemetryFile, string.Join(',', values) + Environment.NewLine);

            if (_lastCleanupDate != DateTime.Today)
            {
                DeleteExpiredFiles("resources-*.csv", TelemetryRetentionDays);
                DeleteExpiredFiles("app-*.log", AppLogRetentionDays);
                _lastCleanupDate = DateTime.Today;
            }
        }
    }

    private static ProcessTotals CaptureCurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        return CaptureProcesses([process]);
    }

    private static ProcessTotals CaptureNamedProcesses(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return CaptureProcesses(processes);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static ProcessTotals CaptureProcesses(IEnumerable<Process> processes)
    {
        var count = 0;
        long workingSetBytes = 0;
        long privateBytes = 0;
        long handles = 0;
        long threads = 0;

        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                count += 1;
                workingSetBytes += process.WorkingSet64;
                privateBytes += process.PrivateMemorySize64;
                handles += process.HandleCount;
                threads += process.Threads.Count;
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // A process can exit while the snapshot is being collected.
            }
        }

        return new ProcessTotals(count, workingSetBytes, privateBytes, handles, threads);
    }

    private static MemorySnapshot ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? new MemorySnapshot(status.MemoryLoad, status.AvailablePhysical, status.AvailablePageFile)
            : new MemorySnapshot(0, 0, 0);
    }

    private static void DeleteExpiredFiles(string pattern, int retentionDays)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDirectory, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (IOException)
            {
                // A current log may briefly be locked by another writer.
            }
            catch (UnauthorizedAccessException)
            {
                // Retention cleanup is best effort and must not stop monitoring.
            }
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Megabytes(ulong bytes) => (bytes / BytesPerMegabyte).ToString("0.0", CultureInfo.InvariantCulture);
    private static string Megabytes(long bytes) => (bytes / BytesPerMegabyte).ToString("0.0", CultureInfo.InvariantCulture);
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record ProcessTotals(int Count, long WorkingSetBytes, long PrivateBytes, long Handles, long Threads);
    private sealed record MemorySnapshot(uint MemoryLoadPercent, ulong AvailablePhysicalBytes, ulong AvailableCommitBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
