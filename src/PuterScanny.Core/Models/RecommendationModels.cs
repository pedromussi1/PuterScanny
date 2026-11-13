namespace PuterScanny.Core.Models;

/// <summary>The keep-or-delete verdict. Ordered from "must keep" to "delete".</summary>
public enum Verdict
{
    /// <summary>Windows needs it. Never deleted.</summary>
    Required = 0,

    /// <summary>Keep: in use, part of an installed program, or simply nothing suggests it can go.</summary>
    Keep = 1,

    /// <summary>Your own file (documents, photos, music…). Only you decide.</summary>
    KeepPersonal = 2,

    /// <summary>Could go, but it's your call: look before deleting.</summary>
    Review = 3,

    /// <summary>Can be deleted without harm (it is recreated or re-downloaded if ever needed).</summary>
    SafeToDelete = 4,

    /// <summary>Safe, and there is a clear benefit to removing it (old temporary files, crash dumps, leftovers…).</summary>
    RecommendDelete = 5,
}

/// <summary>How to act on a verdict.</summary>
public enum CleanupAction
{
    None = 0,
    Delete = 1,

    /// <summary>Remove the whole program through Settings &gt; Apps (or its own uninstaller), not by deleting files.</summary>
    Uninstall = 2,

    /// <summary>Use a Windows tool (Disk Cleanup, Storage Sense, Empty Recycle Bin…) instead of deleting by hand.</summary>
    UseWindowsTool = 3,

    /// <summary>Several identical copies: keep one, the others can go.</summary>
    KeepOneCopy = 4,

    /// <summary>Clear it from inside the app that owns it (browser settings, Steam, Docker…).</summary>
    ClearInApp = 5,
}

/// <summary>Special facts found about an item, stored so the recommendation can be re-explained later.</summary>
[Flags]
public enum FactFlags
{
    None = 0,
    EmptyFolder = 1 << 0,
    BrokenShortcut = 1 << 1,
    Leftover = 1 << 2,
    InstallerInDownloads = 1 << 3,
    PartialDownload = 1 << 4,
    MatchesInstalledProgram = 1 << 5,
    Duplicate = 1 << 6,
    DuplicateKeeper = 1 << 7,
}

/// <summary>One piece of evidence. Positive weight points toward deleting, negative toward keeping.</summary>
public sealed record Evidence(int Weight, string Text);

public sealed record Recommendation(
    Verdict Verdict,
    int Confidence,
    CleanupAction Action,
    string Headline,
    IReadOnlyList<Evidence> Evidence)
{
    public bool IsDeletable => Verdict is Verdict.SafeToDelete or Verdict.RecommendDelete;
}

/// <summary>A program PuterScanny suggests uninstalling.</summary>
public sealed record UninstallSuggestion(long AppId, int Confidence, string Reason);

/// <summary>A set of identical files.</summary>
public sealed record DuplicateGroup(long GroupId, long FileSize, int Copies, long WastedBytes, string Keeper, IReadOnlyList<string> Others);
