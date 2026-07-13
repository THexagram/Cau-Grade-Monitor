namespace CauGradeMonitor.Desktop.Models;

public enum ServicePhase
{
    Stopped,
    Starting,
    Running,
    Degraded,
    Error
}

public sealed record RuntimeStatus(
    ServicePhase VpnPhase,
    string VpnText,
    ServicePhase MonitorPhase,
    string MonitorText,
    DateTimeOffset? VpnConnectedAt,
    DateTimeOffset? LastCheckAt);

public sealed record GradeSnapshot(
    int Rows,
    string Gpa,
    int Required,
    int CountedRequired,
    double Credits,
    string Source,
    DateTimeOffset CheckedAt);

public sealed record LogEntry(DateTimeOffset Timestamp, string Source, string Message, ServicePhase Level);
