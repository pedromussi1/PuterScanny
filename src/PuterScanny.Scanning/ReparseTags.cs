namespace PuterScanny.Scanning;

/// <summary>Known NTFS reparse tags and the rule for which folders are safe to descend into.</summary>
public static class ReparseTags
{
    public const uint MountPoint = 0xA0000003;      // junctions and volume mount points
    public const uint Symlink = 0xA000000C;
    public const uint AppExecLink = 0x8000001B;     // WindowsApps execution aliases
    public const uint Dedup = 0x80000013;
    public const uint CloudMask = 0xFFFF0FFF;
    public const uint CloudBase = 0x9000001A;       // OneDrive and other Cloud Files API providers
    public const uint NameSurrogateBit = 0x20000000;

    public static bool IsCloud(uint tag) => (tag & CloudMask) == CloudBase;

    /// <summary>
    /// A "name surrogate" (junction, symlink, mount point) points at another location, so following it would
    /// count the same files twice or loop forever. Anything else (cloud folders, dedup, etc.) is a real folder.
    /// </summary>
    public static bool ShouldDescend(uint tag) => (tag & NameSurrogateBit) == 0;

    public static string Describe(uint tag) => tag switch
    {
        MountPoint => "Junction / mount point",
        Symlink => "Symbolic link",
        AppExecLink => "App execution alias",
        Dedup => "Deduplicated file",
        _ when IsCloud(tag) => "Cloud-synced item",
        0 => "None",
        _ => $"Reparse point 0x{tag:X8}",
    };
}
