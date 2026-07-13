using System.Text.Json;
using CauGradeMonitor.Desktop.Models;

namespace CauGradeMonitor.Desktop.Services;

public static class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GradeSnapshot? Load()
    {
        if (!File.Exists(AppPaths.SnapshotFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<GradeSnapshot>(File.ReadAllText(AppPaths.SnapshotFile), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(GradeSnapshot snapshot)
    {
        try
        {
            var temporaryFile = AppPaths.SnapshotFile + ".tmp";
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporaryFile, AppPaths.SnapshotFile, true);
        }
        catch
        {
            // A display cache failure must not interrupt grade monitoring.
        }
    }
}
