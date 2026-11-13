namespace PuterScanny.Core.Models;

/// <summary>Where we learned that a program is installed.</summary>
public enum AppSource
{
    InstalledProgram = 0,   // Apps & features (Uninstall registry keys)
    StoreApp = 1,           // Microsoft Store / MSIX / AppX package
    Steam = 2,
    EpicGames = 3,
    Gog = 4,
    Chocolatey = 5,
    Scoop = 6,
}

/// <summary>A program found on this PC, plus how much of the scanned area it was found to occupy.</summary>
public sealed record InstalledApp
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    public DateTime? InstallDate { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public AppSource Source { get; init; }
    public string? UninstallCommand { get; init; }

    /// <summary>Hidden from Apps &amp; features (a component of a bigger product, runtime or update).</summary>
    public bool IsSystemComponent { get; init; }

    /// <summary>Shared runtime/framework other apps depend on (for example VC++ runtimes, Store frameworks).</summary>
    public bool IsFramework { get; init; }

    /// <summary>Stable identifier from the source: registry key, package full name, Steam app id, …</summary>
    public string? SourceKey { get; init; }

    /// <summary>Other folders that belong to this app (for example a Store app's data folder).</summary>
    public IReadOnlyList<string> ExtraFolders { get; init; } = [];

    /// <summary>Exact files a Windows Installer (MSI) product registered as its own.</summary>
    public IReadOnlyList<string> OwnedFiles { get; init; } = [];

    /// <summary>How many exact files Windows Installer lists for this app (stored count; the list itself is not kept).</summary>
    public long OwnedFileCount { get; init; }

    public long MeasuredBytes { get; init; }
    public long MeasuredFiles { get; init; }

    /// <summary>When the program (or anything of it) was last used, from usage history.</summary>
    public DateTime? LastUsedUtc { get; init; }
    public int UseCount { get; init; }
    public TimeSpan ActiveTime { get; init; }

    /// <summary>When above 0, PuterScanny suggests uninstalling this program, with this confidence.</summary>
    public int UninstallConfidence { get; init; }
    public string? UninstallReason { get; init; }
}

public enum SignatureState
{
    NotChecked = 0,
    Valid = 1,
    NotSigned = 2,

    /// <summary>The file was changed after it was signed (the signature no longer matches).</summary>
    Tampered = 3,

    /// <summary>Signed, but by a certificate Windows does not trust.</summary>
    Untrusted = 4,
    Expired = 5,
    Revoked = 6,
    Error = 7,
}

public enum SignatureKind
{
    None = 0,

    /// <summary>The signature is inside the file.</summary>
    Embedded = 1,

    /// <summary>The file's hash is listed in a signed Windows catalog (how most Windows system files are signed).</summary>
    Catalog = 2,
}

/// <summary>Facts read from a Windows program file (EXE, DLL, SYS, …).</summary>
public sealed record PeInfo
{
    public string Machine { get; init; } = "Unknown";
    public string Subsystem { get; init; } = "Unknown";
    public bool IsDll { get; init; }
    public bool IsDriver { get; init; }
    public bool IsDotNet { get; init; }
    public DateTime? LinkTimeUtc { get; init; }

    public string? CompanyName { get; init; }
    public string? ProductName { get; init; }
    public string? FileDescription { get; init; }
    public string? FileVersion { get; init; }
    public string? ProductVersion { get; init; }
    public string? OriginalFilename { get; init; }
    public string? InternalName { get; init; }
    public string? LegalCopyright { get; init; }

    public bool HasEmbeddedSignature { get; init; }
    public SignatureState SignatureState { get; init; }
    public SignatureKind SignatureKind { get; init; }
    public string? SignedBy { get; init; }
    public int SignatureError { get; init; }
}

public enum ReadStatus
{
    NotRead = 0,
    Read = 1,

    /// <summary>Deliberately not opened (empty file, or cloud file that would be downloaded).</summary>
    Skipped = 2,
    AccessDenied = 3,
    InUse = 4,
    Error = 5,
}

/// <summary>What a file's contents say it is, regardless of its name.</summary>
public sealed record ContentIdentity
{
    public required long EntryId { get; init; }
    public ReadStatus ReadStatus { get; init; }
    public string? FormatId { get; init; }
    public string? FormatName { get; init; }
    public string? Category { get; init; }

    /// <summary>The content does not match the file's extension (for example a program named ".jpg").</summary>
    public bool ExtensionMismatch { get; init; }

    public PeInfo? Pe { get; init; }
}

/// <summary>What the analysis found about one file or folder, as shown in the report.</summary>
public sealed record AnalysisInfo
{
    public long? OwnerAppId { get; init; }
    public string? OwnerAppName { get; init; }

    /// <summary>True when this exact item (not just a parent folder) was registered as the app's.</summary>
    public bool OwnerIsExact { get; init; }

    public string? LocationRuleId { get; init; }
    public string? Category { get; init; }
    public string? TypeLabel { get; init; }
    public string? FormatId { get; init; }
    public bool ExtensionMismatch { get; init; }
    public ReadStatus ReadStatus { get; init; }

    /// <summary>The strongest reason to keep this item itself.</summary>
    public Protection Protection { get; init; }

    /// <summary>For folders: the strongest protection of anything inside (so the folder must not be deleted as a whole).</summary>
    public Protection ContainedProtection { get; init; }

    /// <summary>When this item was last used, according to usage history (not file dates).</summary>
    public DateTime? LastUsedUtc { get; init; }

    /// <summary>Highest use count any one source reported (0 when unknown).</summary>
    public int UseCount { get; init; }

    public TimeSpan ActiveTime { get; init; }

    /// <summary>For folders: when anything inside was last used.</summary>
    public DateTime? ContainedLastUsedUtc { get; init; }

    /// <summary>For folders: when anything inside last changed.</summary>
    public DateTime? ContainedLastModifiedUtc { get; init; }

    // ---- keep-or-delete recommendation
    public Verdict? Verdict { get; init; }
    public int Confidence { get; init; }
    public CleanupAction Action { get; init; }
    public string? Headline { get; init; }
    public FactFlags Facts { get; init; }

    /// <summary>For folders: bytes inside that are safe or recommended to delete.</summary>
    public long ReclaimBytes { get; init; }

    /// <summary>For folders: bytes inside offered for review.</summary>
    public long ReviewBytes { get; init; }

    /// <summary>For folders: bytes inside that a Windows tool can free.</summary>
    public long ToolBytes { get; init; }

    /// <summary>For folders: the main reasons space can be freed, e.g. "Browser cache: 2.1 GB".</summary>
    public string? TopReasons { get; init; }
}

public sealed record CategoryStat(string Category, long Files, long Bytes);

public sealed record ExtensionStat(string Extension, long Files, long Bytes, string? Label, string? Category, bool Known, string? WindowsTypeName, string? OpensWith);
