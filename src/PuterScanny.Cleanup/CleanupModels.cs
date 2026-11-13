using PuterScanny.Core.Models;

namespace PuterScanny.Cleanup;

public enum RemovalMethod
{
    /// <summary>Windows' Recycle Bin: familiar, restorable from the desktop too.</summary>
    RecycleBin = 0,

    /// <summary>PuterScanny's own safe-keeping folder on the same drive: instant, no size limits, restorable from the app.</summary>
    Quarantine = 1,
}

/// <summary>Something the user put in the clean-up basket.</summary>
public sealed record CleanupItem(
    long EntryId,
    string Path,
    bool IsFolder,
    long Size,
    DateTime ModifiedUtcAtScan,
    long FileCountAtScan,
    Verdict Verdict,
    CleanupAction Action,
    string Reason);

public enum ItemStatus
{
    Removed = 0,
    Skipped = 1,
    Failed = 2,
    Restored = 3,
    Purged = 4,
}

/// <summary>One item of a clean-up, kept so it can be put back.</summary>
public sealed record CleanupRecord
{
    public required string OriginalPath { get; init; }
    public bool IsFolder { get; init; }
    public long Size { get; init; }
    public long EntryId { get; init; }
    public ItemStatus Status { get; set; }
    public string Note { get; set; } = "";
    public RemovalMethod Method { get; init; }

    /// <summary>Where it is now: the quarantine copy, or the Recycle Bin's $R file.</summary>
    public string? StoredAt { get; set; }

    /// <summary>The Recycle Bin's $I information file, removed on restore.</summary>
    public string? RecycleInfoFile { get; set; }
}

public sealed record CleanupBatch
{
    public required string Id { get; init; }
    public DateTime StartedUtc { get; init; }
    public RemovalMethod Method { get; init; }
    public string? ScanDatabase { get; init; }
    public string? RestorePoint { get; set; }
    public List<CleanupRecord> Items { get; init; } = [];

    public long RemovedBytes => Items.Where(i => i.Status == ItemStatus.Removed).Sum(i => i.Size);
    public int RemovedCount => Items.Count(i => i.Status == ItemStatus.Removed);
}

public sealed record CleanupProgress(int Done, int Total, string Current);
