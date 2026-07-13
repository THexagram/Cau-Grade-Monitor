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

public sealed record GradeCourse(
    string Term,
    string Code,
    string Name,
    string Credit,
    string Score,
    string Type,
    bool IncludedInGpa);

public sealed record GradeSnapshot(
    int Rows,
    string Gpa,
    int Required,
    int CountedRequired,
    double Credits,
    string Source,
    DateTimeOffset CheckedAt,
    IReadOnlyList<GradeCourse>? Courses = null,
    string GpaScope = "required_and_sports");

public sealed record LogEntry(DateTimeOffset Timestamp, string Source, string Message, ServicePhase Level);
