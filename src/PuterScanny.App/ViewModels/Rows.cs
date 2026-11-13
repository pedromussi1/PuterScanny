using PuterScanny.Core.Models;

namespace PuterScanny.App.ViewModels;

public sealed record DriveOption(string Root, string Display);

public enum DetailKind
{
    Row,
    Header,
    Warning,
    Note,
}

public sealed record DetailRow(string Label, string Value, DetailKind Kind = DetailKind.Row)
{
    public bool IsHeader => Kind == DetailKind.Header;
    public bool IsWarning => Kind == DetailKind.Warning;
    public bool IsNote => Kind == DetailKind.Note;
    public bool IsRow => Kind is DetailKind.Row or DetailKind.Warning;
}

public sealed record FileRow(
    string Name, string Folder, long Size, string SizeText, DateTime Modified, string ModifiedText, string Path,
    string WhatItIs, string BelongsTo, string MustKeep);

public sealed record IssueRow(string Kind, string Path, string Message);

public sealed record AppRow(
    string Name, string Publisher, string Version, string Kind, string InstalledText, DateTime? Installed,
    long Size, string SizeText, long Files, string Location, string Source,
    string LastUsedText, DateTime? LastUsed, string TimesUsedText, int TimesUsed, string TimeInUseText, double HoursInUse,
    string Suggestion = "");

/// <summary>An item in the clean-up lists; ticking it puts it in the clean-up basket.</summary>
public sealed partial class CleanupRow(
    string name, string verdict, string why, long size, string sizeText, int confidence, string path, string kind,
    long entryId, bool isFolder, DateTime modifiedUtc, long fileCount, Verdict verdictValue, CleanupAction actionValue)
    : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Name { get; } = name;
    public string Verdict { get; } = verdict;
    public string Why { get; } = why;
    public long Size { get; } = size;
    public string SizeText { get; } = sizeText;
    public int Confidence { get; } = confidence;
    public string Path { get; } = path;
    public string Kind { get; } = kind;
    public long EntryId { get; } = entryId;
    public bool IsFolder { get; } = isFolder;
    public DateTime ModifiedUtc { get; } = modifiedUtc;
    public long FileCount { get; } = fileCount;
    public Verdict VerdictValue { get; } = verdictValue;
    public CleanupAction ActionValue { get; } = actionValue;

    /// <summary>Only items that are removed by deleting can go in the basket (not Windows tools or uninstalls).</summary>
    public bool CanSelect => ActionValue is CleanupAction.Delete or CleanupAction.KeepOneCopy;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsChecked { get; set; }
}

public sealed record BatchRow(string Id, string When, string Method, string Summary, string SizeText, bool CanUndo, string Status);

public sealed record BatchItemRow(string Path, string SizeText, string Status, string Note);

public sealed record UninstallRow(string Name, string SizeText, long Size, int Confidence, string Reason, string LastUsed, string Publisher);

public sealed record DuplicateRow(string Keep, int Copies, string SizeText, long Wasted, string WastedText, string Others);

public sealed record UsageSourceRow(string Source, string Status, int Items, string Note);

public sealed record RecentFileRow(string Name, string LastUsedText, DateTime LastUsed, string TimesUsed, string WhatItIs, string Folder, string Path);

public sealed record ProtectionRow(string Level, long Files, string SizeText, long Bytes, string Meaning);

public sealed record ReferenceRow(string Kind, string Level, string Path, string Why);

public sealed record CategoryRow(string Name, string Description, long Files, long Bytes, string SizeText, double Percent);

public sealed record ExtensionRow(string Extension, string Label, string Category, long Files, long Bytes, string SizeText, string OpensWith);
