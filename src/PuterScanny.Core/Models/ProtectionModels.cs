namespace PuterScanny.Core.Models;

/// <summary>
/// How strongly something must be kept, from the hard facts we found. These are vetoes: the keep-or-delete
/// advice may never suggest deleting anything at <see cref="UninstallOnly"/> or above.
/// Higher values are stronger.
/// </summary>
public enum Protection
{
    None = 0,

    /// <summary>Something refers to it in a softer way (a disabled startup entry, a folder on the PATH, a sync folder…).</summary>
    Caution = 1,

    /// <summary>Part of an installed program: remove it by uninstalling the program, never by deleting files.</summary>
    UninstallOnly = 2,

    /// <summary>A running program is using it right now.</summary>
    InUse = 3,

    /// <summary>Windows or an active service, driver, task or registration needs it.</summary>
    Required = 4,
}

/// <summary>Why something is protected.</summary>
public enum ReferenceKind
{
    WindowsProtected = 0,   // Windows Resource Protection
    Service = 1,
    Driver = 2,
    ScheduledTask = 3,
    Startup = 4,
    ComComponent = 5,
    FileAssociation = 6,
    Font = 7,
    RunningProgram = 8,
    LoadedByProgram = 9,
    Locked = 10,
    Uninstaller = 11,
    PathFolder = 12,
    BootOrRecovery = 13,
}

/// <summary>One concrete thing that depends on a file or folder.</summary>
public sealed record SystemReference(ReferenceKind Kind, Protection Level, string Detail);
