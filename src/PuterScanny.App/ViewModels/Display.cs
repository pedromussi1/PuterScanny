using System.IO;
using System.Globalization;
using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;
using PuterScanny.Scanning;

namespace PuterScanny.App.ViewModels;

/// <summary>Shared plain-English formatting for the UI.</summary>
internal static class Display
{
    public static string Date(DateTime utc) =>
        utc == default ? "Unknown" : utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Size(long bytes) => ByteSize.Format(bytes);

    public static string SizeWithBytes(long bytes) =>
        bytes < 1024 ? ByteSize.Format(bytes) : $"{ByteSize.Format(bytes)} ({bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes)";

    public static string Count(long n, string singular, string plural) =>
        $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? singular : plural)}";

    public static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public static string Attributes(FileAttributes a)
    {
        var parts = new List<string>();
        if (a.HasFlag(FileAttributes.ReadOnly)) parts.Add("Read-only");
        if (a.HasFlag(FileAttributes.Hidden)) parts.Add("Hidden");
        if (a.HasFlag(FileAttributes.System)) parts.Add("System");
        if (a.HasFlag(FileAttributes.Archive)) parts.Add("Archive");
        if (a.HasFlag(FileAttributes.Compressed)) parts.Add("Compressed");
        if (a.HasFlag(FileAttributes.Encrypted)) parts.Add("Encrypted");
        if (a.HasFlag(FileAttributes.SparseFile)) parts.Add("Sparse");
        if (a.HasFlag(FileAttributes.Temporary)) parts.Add("Temporary");
        if (a.HasFlag(FileAttributes.Offline)) parts.Add("Offline");
        if (a.HasFlag(FileAttributes.ReparsePoint)) parts.Add("Reparse point");
        if (a.HasFlag(FileAttributes.NotContentIndexed)) parts.Add("Not indexed");
        return parts.Count == 0 ? "None" : string.Join(", ", parts);
    }

    public static string Issue(ScanIssueKind kind) => kind switch
    {
        ScanIssueKind.AccessDenied => "Access denied",
        ScanIssueKind.LinkNotFollowed => "Link not followed",
        ScanIssueKind.EnumerationError => "Listing error",
        ScanIssueKind.StreamEnumerationError => "Hidden streams unreadable",
        ScanIssueKind.SharingViolation => "In use",
        _ => kind.ToString(),
    };

    public static string LinkKind(uint tag) => ReparseTags.Describe(tag);

    public static string SourceName(AppSource source) => source switch
    {
        AppSource.InstalledProgram => "Apps & features",
        AppSource.StoreApp => "Microsoft Store / app package",
        AppSource.Steam => "Steam",
        AppSource.EpicGames => "Epic Games",
        AppSource.Gog => "GOG",
        AppSource.Chocolatey => "Chocolatey",
        AppSource.Scoop => "Scoop",
        _ => source.ToString(),
    };

    /// <summary>"today", "3 days ago", "5 months ago"…</summary>
    public static string Ago(DateTime utc)
    {
        var days = (DateTime.UtcNow - utc).TotalDays;
        if (days < 0) return "just now";
        if (utc.ToLocalTime().Date == DateTime.Now.Date) return "today";
        if (days < 2) return "yesterday";
        if (days < 14) return $"{(int)days} days ago";
        if (days < 60) return $"{(int)(days / 7)} weeks ago";
        if (days < 730) return $"{(int)(days / 30.4)} months ago";
        return $"{days / 365.25:0.#} years ago";
    }

    public static string DateAgo(DateTime utc) => $"{utc.ToLocalTime():d} ({Ago(utc)})";

    public static string Duration(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds} seconds"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} minutes"
        : t.TotalHours < 100 ? $"{t.TotalHours:0.#} hours"
        : $"{t.TotalHours:N0} hours";

    public static string UsageSourceName(UsageSource s) => s switch
    {
        UsageSource.ProgramLaunches => "Program launches",
        UsageSource.RecentItems => "Recent items",
        UsageSource.JumpLists => "Apps' recent lists",
        UsageSource.OfficeRecent => "Office recent documents",
        UsageSource.Steam => "Steam play history",
        UsageSource.LastAccess => "Last-access dates",
        UsageSource.Prefetch => "Program run counts (Prefetch)",
        UsageSource.BackgroundActivity => "Last program runs (BAM)",
        UsageSource.Amcache => "Program inventory (Amcache)",
        _ => s.ToString(),
    };

    public static string VerdictTitle(Verdict v) => v switch
    {
        Verdict.Required => "Required by Windows",
        Verdict.Keep => "Keep",
        Verdict.KeepPersonal => "Keep (your file)",
        Verdict.Review => "Review",
        Verdict.SafeToDelete => "Safe to delete",
        Verdict.RecommendDelete => "Recommended to delete",
        _ => v.ToString(),
    };

    /// <summary>Short tag for tree rows (empty for plain "keep" so the tree stays calm).</summary>
    public static string VerdictTag(Verdict? v, CleanupAction action) => (v, action) switch
    {
        (_, CleanupAction.UseWindowsTool) => "Windows tool",
        (_, CleanupAction.Uninstall) => "Uninstall?",
        (Verdict.RecommendDelete, _) => "Delete",
        (Verdict.SafeToDelete, _) => "Can delete",
        (Verdict.Review, _) => "Review",
        _ => "",
    };

    public static string ActionText(CleanupAction a, string? tool = null) => a switch
    {
        CleanupAction.Delete => "Tick it in the Clean up tab and press Clean up selected (it goes to the Recycle Bin or quarantine, so it can be undone).",
        CleanupAction.Uninstall => "Uninstall the program from Settings > Apps > Installed apps.",
        CleanupAction.UseWindowsTool => "Use the Windows tool named above instead of deleting it by hand.",
        CleanupAction.KeepOneCopy => "Keep one copy and delete the others.",
        CleanupAction.ClearInApp => "Remove it from inside the app that manages it.",
        _ => "",
    };

    public static string ProtectionTitle(Protection p) => p switch
    {
        Protection.Required => "Must be kept",
        Protection.InUse => "In use",
        Protection.UninstallOnly => "Remove only by uninstalling",
        Protection.Caution => "Handle with care",
        _ => "No dependency found",
    };

    public static string ProtectionGlyph(Protection p) => p switch
    {
        Protection.Required => "\uEA18",      // shield
        Protection.InUse => "\uE768",         // play
        Protection.UninstallOnly => "\uE7B8", // package
        Protection.Caution => "\uE7BA",       // warning
        _ => "",
    };

    public static string ReferenceKindName(ReferenceKind k) => k switch
    {
        ReferenceKind.WindowsProtected => "Windows Resource Protection",
        ReferenceKind.Service => "Service",
        ReferenceKind.Driver => "Driver",
        ReferenceKind.ScheduledTask => "Scheduled task",
        ReferenceKind.Startup => "Starts with Windows",
        ReferenceKind.ComComponent => "Registered component",
        ReferenceKind.FileAssociation => "Opens a file type",
        ReferenceKind.Font => "Installed font",
        ReferenceKind.RunningProgram => "Running program",
        ReferenceKind.LoadedByProgram => "Loaded by a program",
        ReferenceKind.Locked => "Locked file",
        ReferenceKind.Uninstaller => "Uninstaller",
        ReferenceKind.PathFolder => "On the PATH",
        ReferenceKind.BootOrRecovery => "Boot / recovery",
        _ => k.ToString(),
    };

    public static string Signature(PeInfo pe)
    {
        string by = pe.SignedBy is { } s ? $" by {s}" : "";
        string how = pe.SignatureKind == SignatureKind.Catalog ? " (through a Windows security catalog)" : "";
        return pe.SignatureState switch
        {
            SignatureState.Valid => $"Valid — signed{by}{how}. The file is unchanged since it was signed.",
            SignatureState.NotSigned => "Not signed. Common for small tools and older programs; it means the publisher cannot be verified.",
            SignatureState.Tampered => $"Broken — the file was changed after it was signed{by}. Treat it with suspicion.",
            SignatureState.Untrusted => $"Signed{by}, but Windows does not trust that certificate.",
            SignatureState.Expired => $"Signed{by}, but the certificate has expired and the signature has no timestamp.",
            SignatureState.Revoked => $"Signed{by}, but that certificate was revoked. Treat it with suspicion.",
            SignatureState.Error => (uint)pe.SignatureError switch
            {
                0x80096005 => $"Signed{by}, but the signature's timestamp could not be verified.",
                0x8009200D or 0x80092003 => "The signature data is damaged and could not be read.",
                _ => $"Could not be checked (Windows reported 0x{pe.SignatureError:X8}).",
            },
            _ => "Not checked.",
        };
    }
}
