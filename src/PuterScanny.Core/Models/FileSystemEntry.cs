namespace PuterScanny.Core.Models;

/// <summary>Facts the scanner records about an entry beyond its basic attributes.</summary>
[Flags]
public enum EntryFlags
{
    None = 0,

    /// <summary>A junction, symbolic link or mount point. Recorded, but its target is not scanned (avoids loops and double counting).</summary>
    ReparsePointNotFollowed = 1 << 0,

    /// <summary>Cloud-sync placeholder (OneDrive etc.) whose data is not stored on this PC. Never opened, so it is never downloaded.</summary>
    CloudPlaceholder = 1 << 1,

    Compressed = 1 << 2,
    Sparse = 1 << 3,

    /// <summary>Has named alternate data streams (hidden extra content attached to the file).</summary>
    HasAlternateStreams = 1 << 4,

    /// <summary>The folder's contents could not be listed (for example access denied).</summary>
    EnumerationFailed = 1 << 5,

    /// <summary>Size on disk was estimated by rounding up to the cluster size, not measured.</summary>
    SizeOnDiskEstimated = 1 << 6,

    /// <summary>Another name (hard link) for a file already listed elsewhere: its space is counted only once, at the first name.</summary>
    HardLinkSecondary = 1 << 7,
}

/// <summary>One file or folder found during a scan.</summary>
public sealed record FileSystemEntry
{
    public required long Id { get; init; }
    public long? ParentId { get; init; }
    public required string Name { get; init; }

    /// <summary>Normal display path (no <c>\\?\</c> prefix).</summary>
    public required string Path { get; init; }

    public required bool IsDirectory { get; init; }
    public int Depth { get; init; }

    /// <summary>Logical size in bytes (files only; 0 for folders).</summary>
    public long Size { get; init; }

    /// <summary>Space actually allocated on disk (files only; 0 for folders).</summary>
    public long SizeOnDisk { get; init; }

    public FileAttributes Attributes { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime AccessedUtc { get; init; }
    public uint ReparseTag { get; init; }
    public EntryFlags Flags { get; init; }

    /// <summary>Bytes held in named alternate data streams of this entry.</summary>
    public long StreamBytes { get; init; }

    /// <summary>Where a junction/symlink points, when it could be read.</summary>
    public string? LinkTarget { get; init; }

    // Totals: for folders they cover everything beneath; for files they equal the file's own values.
    public long TotalSize { get; init; }
    public long TotalSizeOnDisk { get; init; }
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }

    /// <summary>Identity and ownership, when the analysis stage has run.</summary>
    public AnalysisInfo? Analysis { get; init; }

    public string? Extension => IsDirectory ? null : System.IO.Path.GetExtension(Name) is { Length: > 0 } e ? e.ToLowerInvariant() : null;
}

/// <summary>Recursive totals for a folder, computed once the whole tree is scanned.</summary>
public sealed record DirectoryTotals(
    long Id,
    long TotalSize,
    long TotalSizeOnDisk,
    long FileCount,
    long DirectoryCount,
    EntryFlags ExtraFlags);

/// <summary>A named alternate data stream attached to a file or folder.</summary>
public sealed record AlternateStream(long EntryId, string Name, long Size);
