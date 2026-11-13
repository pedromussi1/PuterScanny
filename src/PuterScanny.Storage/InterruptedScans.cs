using System.Globalization;
using PuterScanny.Core.Models;

namespace PuterScanny.Storage;

/// <summary>A saved scan whose file list is complete but whose analysis was interrupted (crash, power cut, Cancel, app closed).</summary>
public sealed record InterruptedScan(string FilePath, string RootPath, DateTime StartedUtc, long Files, long FilesChecked, bool OwnershipDone);

public static class InterruptedScans
{
    private const int MaxScansToCheck = 20;

    /// <summary>The most recent interrupted scan of <paramref name="rootPath"/>, or null.</summary>
    public static InterruptedScan? Find(string rootPath, string? folder = null)
    {
        folder ??= ScanDatabase.DefaultScansFolder;
        if (!Directory.Exists(folder)) return null;

        var candidates = Directory.GetFiles(folder, "scan-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(MaxScansToCheck);

        foreach (string file in candidates)
        {
            try
            {
                using var db = ScanDatabase.Open(file); // read-write: SQLite may need to recover its log after a crash
                if (db.GetMeta("schema_version") != ScanDatabase.SchemaVersion.ToString(CultureInfo.InvariantCulture)) continue;
                if (db.GetMeta("resume.declined") == "1") continue;

                var summary = db.GetSummary();
                if (summary is null || !SamePath(summary.RootPath, rootPath)) continue;

                // The file list itself only takes minutes, so a scan interrupted during listing is simply redone.
                if (summary.Status != ScanStatus.Completed) continue;

                string? status = db.GetMeta("analysis.status");
                if (status is null or "complete") continue;

                return new InterruptedScan(file, summary.RootPath, summary.StartedUtc, summary.FileCount,
                    db.CountIdentifiedFiles(), db.GetMeta("analysis.owners") == "done");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // A damaged or locked scan file is just not offered for resuming.
            }
        }
        return null;
    }

    /// <summary>Remembers that the user chose a fresh scan, so this one is not offered again.</summary>
    public static void Decline(string file)
    {
        using var db = ScanDatabase.Open(file);
        db.SetMeta("resume.declined", "1");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
