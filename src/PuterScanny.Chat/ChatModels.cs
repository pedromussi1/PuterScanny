using PuterScanny.Core.Models;
using PuterScanny.Storage;

namespace PuterScanny.Chat;

/// <summary>What one lookup does. Every lookup only reads the scan; none of them changes anything.</summary>
public enum LookupKind
{
    /// <summary>Totals for a folder: size, what PuterScanny recommends, biggest items inside.</summary>
    Overview,

    /// <summary>Items matching filters (biggest, deletable, unused, of a kind…).</summary>
    List,

    /// <summary>Everything known about one named file or folder.</summary>
    Item,

    Duplicates,
    Programs,

    /// <summary>Space used per kind of file.</summary>
    Types,
}

public enum ListFilter
{
    Any,

    /// <summary>Recommended to delete or safe to delete.</summary>
    Delete,

    Review,

    /// <summary>Keep, Keep (your file) and Required by Windows.</summary>
    Keep,

    /// <summary>Required by Windows.</summary>
    Required,

    /// <summary>"No reason to remove".</summary>
    NoReason,

    /// <summary>Nothing identifies it.</summary>
    Unidentified,

    /// <summary>Game-mod downloads (Nexus Mods naming).</summary>
    Mods,

    /// <summary>Left behind by programs that are no longer installed.</summary>
    Leftovers,

    EmptyFolders,
    BrokenShortcuts,

    /// <summary>Downloads that never finished.</summary>
    PartialDownloads,
}

/// <summary>One read-only question to the scan.</summary>
public sealed record LookupRequest
{
    public LookupKind Kind { get; init; }

    /// <summary>A folder name or path to look inside; null = the folder the chat is about.</summary>
    public string? Folder { get; init; }

    /// <summary>The item to describe (Item), or text the names must contain (List).</summary>
    public string? Name { get; init; }

    public EntryKind ItemKind { get; init; }
    public ListFilter Filter { get; init; }

    /// <summary>A knowledge-base category id (video, installer…).</summary>
    public string? Category { get; init; }

    /// <summary>Not opened or changed for this many days.</summary>
    public int? UnusedDays { get; init; }

    /// <summary>Not changed for this many days.</summary>
    public int? OlderDays { get; init; }

    public long? MinBytes { get; init; }
    public EntrySort Sort { get; init; }
    public int Count { get; init; } = 10;
}

/// <summary>A file, folder or program an answer is based on (shown as a row under the answer).</summary>
public sealed record ChatItem
{
    /// <summary>The scan entry, when the item is one (programs may not be).</summary>
    public long? EntryId { get; init; }

    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public long Bytes { get; init; }
    public string SizeText { get; init; } = "";

    /// <summary>PuterScanny's recommendation in words ("Recommended to delete", "Keep"…), or "" when none.</summary>
    public string VerdictText { get; init; } = "";

    public Verdict? Verdict { get; init; }
    public CleanupAction Action { get; init; }

    /// <summary>The one-line reason for the recommendation.</summary>
    public string Headline { get; init; } = "";

    /// <summary>Extra facts for this list (type, last opened, copies…).</summary>
    public string Note { get; init; } = "";

    public bool IsDeletable => Verdict is Core.Models.Verdict.SafeToDelete or Core.Models.Verdict.RecommendDelete;
}

/// <summary>What one lookup found: a plain answer, the items, and the same facts written out for the local AI.</summary>
public sealed record LookupResult
{
    public required LookupRequest Request { get; init; }

    /// <summary>The folder the lookup actually looked in.</summary>
    public required string ScopePath { get; init; }

    /// <summary>A complete, exact answer written from the results (no AI).</summary>
    public required string Answer { get; init; }

    public IReadOnlyList<ChatItem> Items { get; init; } = [];

    /// <summary>The results as numbered lines, for the local AI to answer from.</summary>
    public required string FactsText { get; init; }

    /// <summary>A short description of the lookup ("biggest files in Downloads"), shown under the answer.</summary>
    public required string Description { get; init; }
}

public enum AnswerSource
{
    /// <summary>Recognised question, answered straight from the scan.</summary>
    Scan,

    /// <summary>The local AI chose the lookups and worded the answer; the facts were checked against the scan.</summary>
    LocalAi,

    /// <summary>The local AI chose the lookups, but its wording didn't match the scan, so the plain answer is shown.</summary>
    LocalAiFallback,

    /// <summary>Not understood.</summary>
    Help,
}

public sealed record ChatAnswer
{
    public required string Text { get; init; }
    public IReadOnlyList<ChatItem> Items { get; init; } = [];
    public required AnswerSource Source { get; init; }

    /// <summary>What was looked up, in words.</summary>
    public IReadOnlyList<string> Lookups { get; init; } = [];

    public string? ModelName { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>A language model that runs on this PC (tests use a fake).</summary>
public interface IChatModel
{
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>One system + user prompt; <paramref name="grammar"/> (GBNF) forces the shape of the reply.</summary>
    Task<string> GenerateAsync(string system, string user, int maxTokens, string? grammar, float temperature, CancellationToken ct);
}
