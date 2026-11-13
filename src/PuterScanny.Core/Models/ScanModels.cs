namespace PuterScanny.Core.Models;

public enum ScanMode
{
    /// <summary>Runs with the user's normal rights.</summary>
    Standard = 0,

    /// <summary>Runs elevated for the deepest signals (implemented in a later milestone).</summary>
    Deep = 1,
}

public enum ScanStatus
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
}

public enum ScanIssueKind
{
    /// <summary>Windows refused access to the folder or file.</summary>
    AccessDenied = 0,

    /// <summary>A junction/symlink/mount point that was deliberately not followed.</summary>
    LinkNotFollowed = 1,

    /// <summary>Any other error while listing a folder.</summary>
    EnumerationError = 2,

    /// <summary>Alternate data streams could not be listed.</summary>
    StreamEnumerationError = 3,

    /// <summary>The file is in use in a way that blocked inspection.</summary>
    SharingViolation = 4,
}

/// <summary>Something the scan could not fully inspect. Every one is kept so the report can show nothing was silently skipped.</summary>
public sealed record ScanIssue(string Path, ScanIssueKind Kind, int Win32Error, string Message);

public sealed record ScanOptions
{
    public required string RootPath { get; init; }
    public ScanMode Mode { get; init; } = ScanMode.Standard;

    /// <summary>List hidden alternate data streams on every file (slower, more complete).</summary>
    public bool IncludeAlternateStreams { get; init; } = true;
}

public enum ScanPhase
{
    Preparing,
    Enumerating,
    ComputingTotals,
    Finalizing,
    Done,
    CollectingInventory,
    MatchingOwners,
    IdentifyingFiles,
    FindingDuplicates,
    Recommending,
}

/// <summary>Progress of the whole scan-and-analyse pipeline, ready to show in the UI.</summary>
public sealed record PipelineProgress(
    ScanPhase Phase,
    string Headline,
    string? Detail,
    double? Fraction,
    string? CurrentPath,
    ScanProgress? Scan = null);

/// <summary>Which analysis stages to run (all on by default, because thoroughness matters more than speed).</summary>
public sealed record AnalysisOptions
{
    /// <summary>Open every file to learn its true format and, for programs, publisher and version.</summary>
    public bool IdentifyContents { get; init; } = true;

    /// <summary>Check digital signatures of program files (offline).</summary>
    public bool VerifySignatures { get; init; } = true;

    /// <summary>Ask Windows Installer which exact files each MSI program owns.</summary>
    public bool IncludeMsiComponents { get; init; } = true;

    /// <summary>Parallel workers for reading files; 0 = choose automatically.</summary>
    public int Workers { get; init; }

    /// <summary>
    /// Go easy on the PC: fewer files at once, Windows' low-priority disk mode, a cap on files opened per second,
    /// and only one huge program file read in full at a time. Slower, but antivirus and drivers are never flooded.
    /// </summary>
    public bool Gentle { get; init; } = true;

    /// <summary>Upper limit on files opened per second in gentle mode.</summary>
    public int GentleFilesPerSecond { get; init; } = 600;
}

public sealed record ScanProgress(
    ScanPhase Phase,
    long FilesScanned,
    long DirectoriesScanned,
    long BytesScanned,
    long BytesOnDiskScanned,
    long IssueCount,
    string? CurrentPath,
    TimeSpan Elapsed,
    double? EstimatedFraction);

/// <summary>Headline facts about a scan, stored with its results.</summary>
public sealed record ScanSummary
{
    public required string RootPath { get; init; }
    public ScanMode Mode { get; init; }
    public bool IsElevated { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public ScanStatus Status { get; init; }
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public long TotalSize { get; init; }
    public long TotalSizeOnDisk { get; init; }
    public long IssueCount { get; init; }
    public long StreamCount { get; init; }
    public long StreamBytes { get; init; }
    public long ClusterSize { get; init; }
    public string? MachineName { get; init; }
    public string? AppVersion { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>How the file list was made, e.g. "Read the drive's file table directly (Deep scan)".</summary>
    public string? Method { get; init; }

    public TimeSpan? Duration => CompletedUtc is { } end ? end - StartedUtc : null;
}
