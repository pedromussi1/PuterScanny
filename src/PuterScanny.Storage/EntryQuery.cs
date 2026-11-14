using PuterScanny.Core.Models;

namespace PuterScanny.Storage;

public enum EntryKind
{
    Any,
    File,
    Folder,
}

public enum EntrySort
{
    /// <summary>Biggest first.</summary>
    Size,

    /// <summary>Least recently changed first.</summary>
    Oldest,

    /// <summary>Most recently changed first.</summary>
    Newest,

    /// <summary>Opened longest ago (or never, as far as Windows' records show) first.</summary>
    LeastRecentlyUsed,
}

/// <summary>A read-only question to the scan: which items match, biggest (or oldest…) first.</summary>
public sealed record EntryQuery
{
    /// <summary>A folder path: only items inside it count. Null = the whole scan.</summary>
    public string? Scope { get; init; }

    public EntryKind Kind { get; init; }

    /// <summary>Only items with one of these recommendations.</summary>
    public IReadOnlyCollection<Verdict>? Verdicts { get; init; }

    /// <summary>True: only "No reason to remove" items. False: leave them out. Null: either.</summary>
    public bool? NoEvidence { get; init; }

    /// <summary>Only items with at least one of these findings (mod downloads, leftovers, empty folders…).</summary>
    public FactFlags AnyFacts { get; init; }

    /// <summary>Only items nothing identifies (no owning program, no known location, unknown file type).</summary>
    public bool Unidentified { get; init; }

    /// <summary>Only files of these kinds (knowledge-base category ids, such as "video" or "installer").</summary>
    public IReadOnlyCollection<string>? Categories { get; init; }

    /// <summary>Only items whose name contains this text (any case).</summary>
    public string? NameContains { get; init; }

    /// <summary>Only items neither opened (as far as Windows' records show) nor changed since this time.</summary>
    public DateTime? NotUsedSinceUtc { get; init; }

    /// <summary>Only items last changed before this time.</summary>
    public DateTime? ModifiedBeforeUtc { get; init; }

    public long MinBytes { get; init; }

    /// <summary>
    /// List a matching folder once instead of also listing everything inside it (applies to recommendation and
    /// "unidentified" filters, like the Clean up tab).
    /// </summary>
    public bool TopmostOnly { get; init; }

    public EntrySort Sort { get; init; }

    public int Limit { get; init; } = 10;
}

/// <summary>Files of one kind inside a folder: PuterScanny's label for them ("" when unknown), their category, count and size.</summary>
public sealed record TypeStat(string Label, string Category, long Files, long Bytes);
