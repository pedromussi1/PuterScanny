using System.Runtime.InteropServices;


namespace PuterScanny.Cleanup;

/// <summary>
/// Carries out a clean-up the user confirmed: every item is checked again right before it's touched, moved to the
/// Recycle Bin or quarantine (never deleted outright), and written to the history straight away so it can be undone.
/// </summary>
public sealed class CleanupRunner(SafetyChecks safety, Quarantine quarantine, CleanupHistory history)
{
    public CleanupBatch Run(IReadOnlyList<CleanupItem> items, RemovalMethod method, string? scanDatabase,
        IProgress<CleanupProgress>? progress, CancellationToken ct)
    {
        var batch = new CleanupBatch
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            StartedUtc = DateTime.UtcNow,
            Method = method,
            ScanDatabase = scanDatabase,
        };
        history.Add(batch);

        // Folders first would hide their own files from the per-item checks; remove deepest paths first instead,
        // and skip anything inside a folder that is itself in the basket.
        var ordered = items
            .Where(i => !items.Any(o => o.IsFolder && !ReferenceEquals(o, i) && i.Path.StartsWith(o.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(i => i.Path.Length)
            .ToList();

        int done = 0;
        foreach (var item in ordered)
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(new CleanupProgress(done, ordered.Count, item.Path));

            var record = new CleanupRecord { OriginalPath = item.Path, IsFolder = item.IsFolder, Size = item.Size, EntryId = item.EntryId, Method = method };
            batch.Items.Add(record);

            string? problem;
            try { problem = safety.Check(item); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = ex.Message; }

            if (problem is not null)
            {
                record.Status = ItemStatus.Skipped;
                record.Note = problem;
            }
            else
            {
                Remove(item, record, method, batch.Id, done);
            }
            history.Save(); // after every item: an undo must survive a crash or power cut halfway
            done++;
        }
        progress?.Report(new CleanupProgress(done, ordered.Count, ""));
        return batch;
    }

    private void Remove(CleanupItem item, CleanupRecord record, RemovalMethod method, string batchId, int index)
    {
        try
        {
            if (method == RemovalMethod.RecycleBin && item.Path.Length <= RecycleBin.MaxPath)
            {
                var r = RecycleBin.Send(item.Path);
                if (r.Ok)
                {
                    record.Status = ItemStatus.Removed;
                    record.StoredAt = r.StoredAt;
                    record.RecycleInfoFile = r.InfoFile;
                    record.Note = "Moved to the Recycle Bin.";
                    return;
                }
                if (r.Error == "Cancelled.")
                {
                    record.Status = ItemStatus.Skipped;
                    record.Note = "You chose not to delete it permanently.";
                    return;
                }
                // The Recycle Bin refused (too long, too big, or not available on this drive): keep it safe in quarantine instead.
                record.Note = r.Error + " Put in quarantine instead.";
            }

            record.StoredAt = quarantine.Move(item.Path, batchId, index);
            record.Status = ItemStatus.Removed;
            record.Note = (record.Note.Length > 0 ? record.Note + " " : "") + "Moved to quarantine.";
            if (method == RemovalMethod.RecycleBin) record.Note = record.Note.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            record.Status = ItemStatus.Failed;
            record.Note = $"Couldn't move it: {ex.Message}";
        }
    }

    /// <summary>Puts back everything from a clean-up that's still in the Recycle Bin or quarantine.</summary>
    public (int Restored, int Problems, List<string> Messages) Undo(string batchId, IEnumerable<string>? onlyPaths = null)
    {
        var batch = history.Find(batchId) ?? throw new ArgumentException("Unknown clean-up.", nameof(batchId));
        var only = onlyPaths?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int restored = 0, problems = 0;
        var messages = new List<string>();

        foreach (var rec in batch.Items.Where(i => i.Status == ItemStatus.Removed && (only is null || only.Contains(i.OriginalPath))))
        {
            (bool ok, string message) result;
            try
            {
                result = rec.Method == RemovalMethod.Quarantine || (rec.StoredAt is not null && quarantine.IsInsideQuarantine(rec.StoredAt))
                    ? quarantine.Restore(rec.StoredAt!, rec.OriginalPath)
                    : RecycleBin.Restore(rec.OriginalPath, rec.StoredAt, rec.RecycleInfoFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = (false, ex.Message);
            }

            if (result.ok)
            {
                rec.Status = ItemStatus.Restored;
                restored++;
            }
            else problems++;
            rec.Note = result.message;
            if (!result.ok || result.message != "Restored.") messages.Add($"{rec.OriginalPath}: {result.message}");
        }
        history.Save();
        return (restored, problems, messages);
    }

    /// <summary>Permanently deletes quarantined items (frees their space), optionally only those older than a number of days.</summary>
    public (int Purged, long Bytes) EmptyQuarantine(int olderThanDays = 0)
    {
        int purged = 0;
        long bytes = 0;
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        foreach (var batch in history.Batches.Where(b => b.StartedUtc <= cutoff))
        {
            foreach (var rec in batch.Items.Where(i => i.Status == ItemStatus.Removed && i.StoredAt is not null && quarantine.IsInsideQuarantine(i.StoredAt)))
            {
                try
                {
                    quarantine.Purge(rec.StoredAt!);
                    rec.Status = ItemStatus.Purged;
                    rec.Note = $"Deleted permanently on {DateTime.Now:d}.";
                    purged++;
                    bytes += rec.Size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rec.Note = $"Couldn't delete it from quarantine: {ex.Message}";
                }
            }
        }
        history.Save();
        return (purged, bytes);
    }

    /// <summary>Bytes currently held in quarantine (removed, not yet emptied).</summary>
    public long QuarantinedBytes() =>
        history.Batches.SelectMany(b => b.Items).Where(i => i.Status == ItemStatus.Removed && i.StoredAt is not null && quarantine.IsInsideQuarantine(i.StoredAt)).Sum(i => i.Size);
}

/// <summary>A Windows System Restore point before a clean-up (needs administrator rights).</summary>
public static class RestorePoint
{
    public static (bool Ok, string Message) TryCreate(string description)
    {
        if (!AdminCheckPublic.IsElevated())
            return (false, "Creating a restore point needs administrator rights (Deep scan). Your clean-up can still be undone from PuterScanny.");
        try
        {
            var info = new RESTOREPOINTINFO { dwEventType = 100, dwRestorePtType = 12, llSequenceNumber = 0, szDescription = description };
            if (SRSetRestorePointW(ref info, out var status) && status.nStatus == 0)
                return (true, "Restore point created.");
            return (false, $"Windows didn't create a restore point (code {status.nStatus}). System Protection may be turned off for this drive.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, "System Restore isn't available on this PC.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFO
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFO pRestorePtSpec, out STATEMGRSTATUS pSMgrStatus);
}

internal static class AdminCheckPublic
{
    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
