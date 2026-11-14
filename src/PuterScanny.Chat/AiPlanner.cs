using System.Text;
using System.Text.Json;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Chat;

/// <summary>
/// Asks the local AI which lookups answer a question. Its reply is forced into a fixed JSON shape by a grammar, so it
/// can only pick from PuterScanny's read-only lookups: it never sees the whole scan and never answers from memory.
/// </summary>
public static class AiPlanner
{
    private static readonly string[] Lookups = ["overview", "list", "item", "duplicates", "programs", "types"];
    private static readonly string[] Kinds = ["any", "file", "folder"];
    private static readonly string[] Filters =
        ["any", "delete", "review", "keep", "required", "no_reason", "unidentified", "mods", "leftovers", "empty_folders", "broken_shortcuts", "partial_downloads"];
    private static readonly string[] Sorts = ["size", "oldest", "newest", "least_used"];

    /// <summary>Category ids the model may use (those people ask about; the rest still work through the matcher).</summary>
    public static readonly string[] Categories =
    [
        "document", "image", "video", "audio", "archive", "disk-image", "installer", "executable", "game", "backup",
        "log", "cache", "temporary", "crash-dump", "virtual-machine", "code", "font", "ebook", "database",
    ];

    public static string System(KnowledgeBase kb) => $$"""
        You turn a question about a scan of the user's PC into lookups in PuterScanny's scan database. Reply with JSON only.

        Lookups:
        - overview: a folder's total size, how much PuterScanny says can be deleted or reviewed, and its biggest items.
        - list: files or folders matching filters (biggest, deletable, unused, old, of one kind...).
        - item: everything known about one file or folder: what it is, PuterScanny's recommendation and its reasons. Put its name in "name" (or null for the current folder itself).
        - duplicates: identical copies of files.
        - programs: installed programs. filter "delete" = PuterScanny suggests uninstalling; unused_days = not used for that long.
        - types: space used per kind of file.

        Fields (null when not needed):
        - folder: a folder name or path to look in; null = the current folder.
        - name: an item's name (for "item"), or text the names must contain (for "list").
        - kind: "any", "file" or "folder".
        - filter: "any"; "delete" (PuterScanny says it can be deleted); "review"; "keep"; "required" (Windows needs it); "no_reason" (nothing points either way); "unidentified" (nothing identifies it); "mods" (game-mod downloads); "leftovers" (from programs no longer installed); "empty_folders"; "broken_shortcuts"; "partial_downloads" (downloads that never finished).
        - category: one of {{string.Join(", ", Categories)}}.
        - unused_days: not opened or changed for this many days (365 = a year).
        - older_days: not changed for this many days.
        - min_mb: at least this many megabytes (1 GB = 1024).
        - sort: "size" (biggest first), "oldest", "newest" or "least_used".
        - count: how many items to show (1 to 25).

        Use one lookup, or two when the question needs both. If the question continues an earlier one, keep its folder and filters.

        Examples:
        Question: big videos I haven't watched in a year
        {"lookups":[{"lookup":"list","folder":null,"name":null,"kind":"file","filter":"any","category":"video","unused_days":365,"older_days":null,"min_mb":null,"sort":"size","count":10}]}
        Question: is anything in Downloads worth worrying about?
        {"lookups":[{"lookup":"overview","folder":"Downloads","name":null,"kind":"any","filter":"any","category":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":5},{"lookup":"list","folder":"Downloads","name":null,"kind":"any","filter":"review","category":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}
        Question: why does PuterScanny want to keep setup_old.exe?
        {"lookups":[{"lookup":"item","folder":null,"name":"setup_old.exe","kind":"any","filter":"any","category":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}
        """;

    public static string User(string question, string scopePath, IReadOnlyList<(string Question, string Looked)> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Current folder: {scopePath}");
        if (history.Count > 0)
        {
            sb.AppendLine("Earlier in this chat (newest last):");
            foreach (var (q, looked) in history.TakeLast(3)) sb.AppendLine($"- \"{q}\" → looked up: {looked}");
        }
        sb.Append($"Question: {question}");
        return sb.ToString();
    }

    /// <summary>GBNF grammar: the reply must be exactly this JSON shape, with only these values.</summary>
    public static string Grammar()
    {
        static string Alt(IEnumerable<string> values) => string.Join(" | ", values.Select(v => $"\"\\\"{v}\\\"\""));
        return $$"""
            root ::= "{\"lookups\":[" call ( "," call )? "]}"
            call ::= "{\"lookup\":" lookup ",\"folder\":" text ",\"name\":" text ",\"kind\":" kind ",\"filter\":" filter ",\"category\":" category ",\"unused_days\":" num ",\"older_days\":" num ",\"min_mb\":" num ",\"sort\":" sort ",\"count\":" num "}"
            lookup ::= {{Alt(Lookups)}}
            kind ::= {{Alt(Kinds)}}
            filter ::= {{Alt(Filters)}}
            category ::= "null" | {{Alt(Categories)}}
            sort ::= {{Alt(Sorts)}}
            num ::= "null" | [1-9] [0-9]{0,5}
            text ::= "null" | "\"" char{1,120} "\""
            char ::= [^"\\\x00-\x1F\x7F] | "\\\\"
            """;
    }

    /// <summary>Reads the model's JSON into lookups. Returns an empty list when it isn't usable.</summary>
    public static IReadOnlyList<LookupRequest> Parse(string json)
    {
        var list = new List<LookupRequest>();
        int start = json.IndexOf('{');
        int end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return list;
        try
        {
            using var doc = JsonDocument.Parse(json[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("lookups", out var calls) || calls.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in calls.EnumerateArray().Take(2))
            {
                string? S(string p) => c.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
                int? N(string p) => c.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n > 0 ? n : null;

                LookupKind? kind = S("lookup") switch
                {
                    "overview" => LookupKind.Overview,
                    "list" => LookupKind.List,
                    "item" => LookupKind.Item,
                    "duplicates" => LookupKind.Duplicates,
                    "programs" => LookupKind.Programs,
                    "types" => LookupKind.Types,
                    _ => null,
                };
                if (kind is null) continue;
                string? category = S("category");
                list.Add(new LookupRequest
                {
                    Kind = kind.Value,
                    Folder = S("folder"),
                    Name = S("name"),
                    ItemKind = S("kind") switch { "file" => EntryKind.File, "folder" => EntryKind.Folder, _ => EntryKind.Any },
                    Filter = S("filter") switch
                    {
                        "delete" => ListFilter.Delete,
                        "review" => ListFilter.Review,
                        "keep" => ListFilter.Keep,
                        "required" => ListFilter.Required,
                        "no_reason" => ListFilter.NoReason,
                        "unidentified" => ListFilter.Unidentified,
                        "mods" => ListFilter.Mods,
                        "leftovers" => ListFilter.Leftovers,
                        "empty_folders" => ListFilter.EmptyFolders,
                        "broken_shortcuts" => ListFilter.BrokenShortcuts,
                        "partial_downloads" => ListFilter.PartialDownloads,
                        _ => ListFilter.Any,
                    },
                    Category = category is not null && Categories.Contains(category) ? category : null,
                    UnusedDays = N("unused_days"),
                    OlderDays = N("older_days"),
                    MinBytes = N("min_mb") is int mb ? mb * 1024L * 1024 : null,
                    Sort = S("sort") switch
                    {
                        "oldest" => EntrySort.Oldest,
                        "newest" => EntrySort.Newest,
                        "least_used" => EntrySort.LeastRecentlyUsed,
                        _ => EntrySort.Size,
                    },
                    Count = Math.Clamp(N("count") ?? 10, 1, ScanLookup.MaxCount),
                });
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return list;
    }
}
