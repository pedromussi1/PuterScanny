using System.Globalization;
using System.Text;
using PuterScanny.Analysis;
using PuterScanny.Analysis.Reporting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Chat;

/// <summary>
/// Runs a <see cref="LookupRequest"/> against the scan and writes the exact answer. Everything said comes from the
/// scan database and PuterScanny's own recommendations; nothing is guessed and nothing is changed.
/// </summary>
public sealed class ScanLookup(ScanDatabase db, KnowledgeBase kb, DateTime? nowUtc = null)
{
    public const int MaxCount = 25;

    private readonly DateTime _now = nowUtc ?? DateTime.UtcNow;

    public ScanDatabase Db => db;
    public KnowledgeBase Kb => kb;
    public DateTime NowUtc => _now;

    public LookupResult Run(LookupRequest request, string scopePath)
    {
        var req = request with { Count = Math.Clamp(request.Count, 1, MaxCount) };
        var (scope, folderNote) = ResolveFolder(req.Folder, scopePath);
        var result = req.Kind switch
        {
            LookupKind.Overview => Overview(req, scope),
            LookupKind.Item => Item(req, scope),
            LookupKind.Duplicates => Duplicates(req, scope),
            LookupKind.Programs => Programs(req, scope),
            LookupKind.Types => Types(req, scope),
            _ => List(req, scope),
        };
        return folderNote is null ? result : result with { Answer = folderNote + " " + result.Answer, FactsText = folderNote + "\n" + result.FactsText };
    }

    // ---------------------------------------------------------------- folders

    /// <summary>Finds the folder a question names ("Downloads", "D:\Games"), preferring one inside the current folder.</summary>
    public FileSystemEntry? FindFolder(string name, string scopePath)
    {
        name = name.Trim().Trim('"', '\'');
        if (name.Length == 0) return null;
        if (name.Contains(':') || name.StartsWith('\\'))
            return db.GetEntryByPath(name) is { IsDirectory: true } byPath ? byPath : null;
        if (!FolderNames.Contains(name)) return null;
        foreach (string? scope in new[] { scopePath, null })
        {
            var matches = db.FindByName(name, scope, EntryKind.Folder, 5);
            var exact = matches.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return null;
    }

    private (string Scope, string? Note) ResolveFolder(string? folder, string scopePath)
    {
        if (string.IsNullOrWhiteSpace(folder)) return (scopePath, null);
        if (string.Equals(folder.Trim().TrimEnd('\\'), scopePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return (scopePath, null);
        if (FindFolder(folder, scopePath) is { } found) return (found.Path, null);
        return (scopePath, $"There's no folder called \"{folder.Trim()}\" in this scan, so this is about {ChatText.FolderName(scopePath)} instead.");
    }

    private FileSystemEntry? ScopeEntry(string scope) => db.GetEntryByPath(scope) ?? (string.Equals(db.GetRoot()?.Path, scope, StringComparison.OrdinalIgnoreCase) ? db.GetRoot() : null);

    private HashSet<string>? _folderNames;

    /// <summary>Every folder name in the scan, so a question's words can be checked instantly.</summary>
    private HashSet<string> FolderNames => _folderNames ??= new HashSet<string>(db.GetFolderNames(), StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- overview

    private LookupResult Overview(LookupRequest req, string scope)
    {
        var folder = ScopeEntry(scope);
        string name = ChatText.FolderName(scope);
        if (folder is null)
            return Empty(req, scope, $"overview of {name}", $"{scope} isn't part of this scan.");

        long recommend = 0, safe = 0, review = 0, tool = 0, keep = 0, personal = 0, required = 0, noReason = 0;
        foreach (var (verdict, action, noEvidence, _, bytes) in db.GetVerdictStats(scope))
        {
            if (action == CleanupAction.UseWindowsTool) { tool += bytes; continue; }
            switch (verdict)
            {
                case Verdict.RecommendDelete: recommend += bytes; break;
                case Verdict.SafeToDelete: safe += bytes; break;
                case Verdict.Review: review += bytes; break;
                case Verdict.Keep when noEvidence: noReason += bytes; break;
                case Verdict.Keep: keep += bytes; break;
                case Verdict.KeepPersonal: personal += bytes; break;
                case Verdict.Required: required += bytes; break;
            }
        }

        var children = db.GetChildren(folder.Id, req.Count).Where(c => c.TotalSize > 0).ToList();
        var items = children.Select(c => ToItem(c, scope)).ToList();

        var sb = new StringBuilder();
        sb.Append($"{name} holds {ChatText.Size(folder.TotalSize)} in {ChatText.Count(folder.FileCount, "file", "files")} and {ChatText.Count(folder.DirectoryCount, "folder", "folders")}.");
        // What the folder itself is, before what's inside it ("a mod download PuterScanny marks Review").
        string self = "";
        if (folder.Depth > 0 && folder.Analysis is { } fa && RecommendationExplainer.Explain(db, kb, folder) is { } own)
        {
            if (Profiler.PartOf(folder) is { } part && part.Entry.Analysis is { } pa && RecommendationExplainer.Explain(db, kb, part.Entry) is { } prec &&
                prec.Verdict is Verdict.Review or Verdict.SafeToDelete or Verdict.RecommendDelete)
                self = $"It's part of {part.Entry.Name}, {part.What}, marked {ChatText.Verdict(prec.Verdict, pa.Facts)} ({prec.Headline.TrimEnd('.')}).";
            else if (!own.NoEvidence)
                self = $"The folder itself is marked {ChatText.Verdict(own.Verdict, fa.Facts)} ({own.Headline.TrimEnd('.')}).";
            if (self.Length > 0) sb.Append(' ').Append(self);
        }
        var free = new List<string>();
        if (recommend > 0) free.Add($"{ChatText.Size(recommend)} is recommended to delete");
        if (safe > 0) free.Add($"{ChatText.Size(safe)} is safe to delete");
        if (review > 0) free.Add($"{ChatText.Size(review)} is worth reviewing");
        if (tool > 0) free.Add($"{ChatText.Size(tool)} can be freed with Windows tools");
        sb.Append(free.Count > 0
            ? $" {Sentence(ChatText.Join(free))}."
            : " Nothing here is clearly safe to remove.");
        var kept = new List<string>();
        if (keep > 0) kept.Add($"{ChatText.Size(keep)} to keep");
        if (personal > 0) kept.Add($"{ChatText.Size(personal)} of your own files");
        if (required > 0) kept.Add($"{ChatText.Size(required)} required by Windows");
        if (noReason > 0) kept.Add($"{ChatText.Size(noReason)} with no reason to remove");
        if (kept.Count > 0) sb.Append($" The rest: {ChatText.Join(kept)}.");
        if (items.Count > 0)
            sb.Append($" The biggest things in it are {ChatText.Join(items.Take(3).Select(i => $"{i.Name} ({i.SizeText})").ToList())}.");

        var facts = new StringBuilder();
        facts.AppendLine($"Lookup: overview of {scope}");
        facts.AppendLine($"Total: {ChatText.Size(folder.TotalSize)}, {ChatText.Count(folder.FileCount, "file", "files")}, {ChatText.Count(folder.DirectoryCount, "folder", "folders")}");
        if (self.Length > 0) facts.AppendLine(self);
        facts.AppendLine($"Recommended to delete: {ChatText.Size(recommend)}; Safe to delete: {ChatText.Size(safe)}; Review: {ChatText.Size(review)}; Freeable with Windows tools: {ChatText.Size(tool)}");
        facts.AppendLine($"Keep: {ChatText.Size(keep)}; Keep (your file): {ChatText.Size(personal)}; Required by Windows: {ChatText.Size(required)}; No reason to remove: {ChatText.Size(noReason)}");
        facts.AppendLine("Biggest items inside:");
        AppendItems(facts, items);

        return new LookupResult
        {
            Request = req,
            ScopePath = scope,
            Answer = sb.ToString(),
            Items = items,
            FactsText = facts.ToString(),
            Description = $"overview of {name}",
        };
    }

    // ---------------------------------------------------------------- lists

    public EntryQuery ToQuery(LookupRequest req, string scope)
    {
        IReadOnlyCollection<Verdict>? verdicts = req.Filter switch
        {
            ListFilter.Delete => [Verdict.RecommendDelete, Verdict.SafeToDelete],
            ListFilter.Review => [Verdict.Review],
            ListFilter.Keep => [Verdict.Keep, Verdict.KeepPersonal, Verdict.Required],
            ListFilter.Required => [Verdict.Required],
            ListFilter.NoReason => [Verdict.Keep],
            _ => null,
        };
        return new EntryQuery
        {
            Scope = scope,
            Kind = req.Category is not null ? EntryKind.File : req.ItemKind,
            Verdicts = verdicts,
            NoEvidence = req.Filter switch { ListFilter.NoReason => true, ListFilter.Keep => false, _ => null },
            Unidentified = req.Filter == ListFilter.Unidentified,
            AnyFacts = FactsFor(req.Filter),
            Categories = req.Category is { } c ? [c] : null,
            NameContains = req.Name,
            NotUsedSinceUtc = req.UnusedDays is int u ? _now.AddDays(-u) : null,
            ModifiedBeforeUtc = req.OlderDays is int o ? _now.AddDays(-o) : null,
            MinBytes = req.MinBytes ?? 0,
            TopmostOnly = verdicts is not null || req.Filter == ListFilter.Unidentified || FactsFor(req.Filter) != FactFlags.None,
            Sort = req.Sort,
            Limit = req.Count,
        };
    }

    private static FactFlags FactsFor(ListFilter f) => f switch
    {
        ListFilter.Mods => FactFlags.ModDownload,
        ListFilter.Leftovers => FactFlags.Leftover,
        ListFilter.EmptyFolders => FactFlags.EmptyFolder,
        ListFilter.BrokenShortcuts => FactFlags.BrokenShortcut,
        ListFilter.PartialDownloads => FactFlags.PartialDownload,
        _ => FactFlags.None,
    };

    private LookupResult List(LookupRequest req, string scope)
    {
        var query = ToQuery(req, scope);
        // Folders and what's inside them can both match: list each place once (the folder), like the Clean up tab.
        var entries = new List<FileSystemEntry>();
        foreach (var e in db.QueryEntries(query with { Limit = Math.Min(100, req.Count * 4) }))
        {
            if (entries.Any(p => p.IsDirectory && e.Path.StartsWith(p.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            entries.Add(e);
            if (entries.Count == req.Count) break;
        }
        var items = entries.Select(e => ToItem(e, scope, ListNote(e, req))).ToList();
        string what = Describe(req, scope);
        bool filtered = req.Filter != ListFilter.Any || req.Category is not null || req.UnusedDays is not null || req.OlderDays is not null ||
                        req.MinBytes is not null || req.Name is not null;
        // Findings about places (mod downloads, unidentified folders…) are counted per place; everything else per file,
        // the same way the Overview and Clean up totals are.
        bool perPlace = req.Filter is ListFilter.Unidentified or ListFilter.Mods or ListFilter.Leftovers or ListFilter.EmptyFolders;
        bool totals = (query.Kind != EntryKind.Folder || perPlace) && filtered;
        var (files, bytes) = !totals ? (0, 0) : perPlace ? db.SummarizeTopmost(query) : db.SummarizeFiles(query);
        string countWord = perPlace ? "item" : "file", countWords = perPlace ? "items" : "files";

        var sb = new StringBuilder();
        if (items.Count == 0)
        {
            sb.Append($"I found no {Describe(req, scope, withSort: false)}.");
            if (req.Filter == ListFilter.Delete) sb.Append(" Nothing here is clearly safe to remove.");
        }
        else
        {
            string top = ChatText.Join(items.Take(3).Select(i => $"{i.Name} ({i.SizeText}{(i.VerdictText.Length > 0 ? ", " + i.VerdictText : "")})").ToList());
            if (req.Filter == ListFilter.Delete && files > 0)
            {
                sb.Append($"{ChatText.Size(bytes)} in {ChatText.FolderName(scope)} can be deleted ({ChatText.Count(files, countWord, countWords)}). The biggest: {top}.");
            }
            else
            {
                sb.Append($"The {what}: {top}.");
                if (totals && files > 0)
                    sb.Append($" In total, {ChatText.Count(files, countWord, countWords)} ({ChatText.Size(bytes)}) match.");
            }
            if (req.Filter == ListFilter.Delete)
                sb.Append(" Nothing is deleted until you put items in the clean-up basket and confirm, and every clean-up can be undone.");
            if (req.UnusedDays is not null)
                sb.Append(" \"Not opened\" means Windows has no record of it being opened in that time; some programs don't leave one.");
        }

        var facts = new StringBuilder();
        facts.AppendLine($"Lookup: {what} ({scope})");
        if (totals) facts.AppendLine($"Matching in total: {ChatText.Count(files, countWord, countWords)}, {ChatText.Size(bytes)}");
        AppendItems(facts, items);
        if (items.Count == 0) facts.AppendLine("No items match.");

        return new LookupResult { Request = req, ScopePath = scope, Answer = sb.ToString(), Items = items, FactsText = facts.ToString(), Description = what };
    }

    /// <summary>"biggest videos not opened or changed in a year in Downloads".</summary>
    public string Describe(LookupRequest req, string scope, bool withSort = true)
    {
        string noun = req.Filter switch
        {
            ListFilter.Mods => "mod downloads",
            ListFilter.Leftovers => "leftovers from programs that are no longer installed",
            ListFilter.EmptyFolders => "empty folders",
            ListFilter.BrokenShortcuts => "broken shortcuts",
            ListFilter.PartialDownloads => "unfinished downloads",
            _ when req.Category is { } cat => CategoryNoun(cat),
            _ => req.ItemKind switch { EntryKind.File => "files", EntryKind.Folder => "folders", _ => "items" },
        };
        string sort = req.Sort switch
        {
            EntrySort.Oldest => "oldest",
            EntrySort.Newest => "newest",
            EntrySort.LeastRecentlyUsed => "least recently used",
            _ => "biggest",
        };
        var parts = new List<string>();
        if (withSort) parts.Add(sort);
        if (req.Filter == ListFilter.Unidentified) parts.Add("unidentified");
        parts.Add(noun);
        switch (req.Filter)
        {
            case ListFilter.Delete: parts.Add("that can be deleted"); break;
            case ListFilter.Review: parts.Add("worth reviewing"); break;
            case ListFilter.Keep: parts.Add("to keep"); break;
            case ListFilter.Required: parts.Add("required by Windows"); break;
            case ListFilter.NoReason: parts.Add("with no reason to remove"); break;
        }
        if (req.Name is { Length: > 0 } n) parts.Add($"with \"{n}\" in the name");
        if (req.MinBytes is long min) parts.Add($"of at least {ChatText.Size(min)}");
        if (req.UnusedDays is int u) parts.Add($"not opened or changed in {ChatText.Period(u)}");
        if (req.OlderDays is int o) parts.Add($"not changed in {ChatText.Period(o)}");
        parts.Add($"in {ChatText.FolderName(scope)}");
        return string.Join(" ", parts);
    }

    private string CategoryNoun(string id)
    {
        string name = kb.GetCategory(id)?.Name.ToLowerInvariant() ?? id;
        return name.EndsWith('s') ? name : name + " files";
    }

    private string ListNote(FileSystemEntry e, LookupRequest req)
    {
        var a = e.Analysis;
        var parts = new List<string>();
        if (!e.IsDirectory && a?.TypeLabel is { } t) parts.Add(t);
        if (e.IsDirectory) parts.Add(ChatText.Count(e.FileCount, "file", "files"));
        if (req.UnusedDays is not null || req.Sort == EntrySort.LeastRecentlyUsed)
        {
            var used = a?.LastUsedUtc ?? a?.ContainedLastUsedUtc;
            parts.Add(used is { } u ? $"last opened {ChatText.Ago(u, _now)}" : "no record of being opened");
        }
        if (req.UnusedDays is not null || req.OlderDays is not null || req.Sort is EntrySort.Oldest or EntrySort.Newest)
        {
            var changed = a?.ContainedLastModifiedUtc ?? e.ModifiedUtc;
            // The date it arrived counts too (and a TorrentZIP placeholder date says nothing).
            if (e.CreatedUtc > changed && e.CreatedUtc <= _now || !e.IsDirectory && FileDates.IsTorrentZipDate(e.ModifiedUtc))
                parts.Add($"added {ChatText.Ago(e.CreatedUtc, _now)}");
            else if (changed != default)
                parts.Add($"changed {ChatText.Ago(changed, _now)}");
        }
        return string.Join(" · ", parts);
    }

    // ---------------------------------------------------------------- one item

    /// <summary>Finds the item a question names: exact names first, inside the current folder first.</summary>
    public (FileSystemEntry? Entry, IReadOnlyList<FileSystemEntry> Others) FindItem(string name, string scopePath)
    {
        name = name.Trim().Trim('"', '\'', '?', '.');
        if (name.Length == 0) return (null, []);
        if (name.Contains(":\\")) return (db.GetEntryByPath(name), []);
        foreach (string? scope in new[] { scopePath, null })
        {
            var matches = db.FindByName(name, scope, EntryKind.Any, 8);
            if (matches.Count == 0) continue;
            var exact = matches.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return (exact[0], exact.Skip(1).ToList());
            // Without the extension: "retip" → "retip.exe" only when that's the only such match.
            var stem = matches.Where(m => string.Equals(Path.GetFileNameWithoutExtension(m.Name), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stem.Count == 1) return (stem[0], []);
            // A short word ("iso") inside other names ("isObject.js") isn't what was meant.
            if (name.Length < 4) continue;
            if (matches.Count == 1) return (matches[0], []);
            // A folder and what's inside it, all matching ("Pokemon - Emerald Version" the folder and its .gba): one thing.
            // When the folder holds just that one file, the file is what was meant; otherwise the folder.
            foreach (var folder in matches.Where(m => m.IsDirectory).OrderBy(m => m.Depth))
            {
                string prefix = folder.Path.TrimEnd('\\') + "\\";
                var inside = matches.Where(m => m != folder && m.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (inside.Count != matches.Count - 1) continue;
                var onlyFile = folder.FileCount == 1 ? inside.FirstOrDefault(m => !m.IsDirectory) : null;
                return (onlyFile ?? folder, []);
            }
            return (null, matches);
        }
        return (null, []);
    }

    private ItemProfiler? _profiler;
    private ItemProfiler Profiler => _profiler ??= new ItemProfiler(db);

    private LookupResult Item(LookupRequest req, string scope)
    {
        FileSystemEntry? e;
        IReadOnlyList<FileSystemEntry> others;
        if (string.IsNullOrWhiteSpace(req.Name)) (e, others) = (ScopeEntry(scope), []);
        else (e, others) = FindItem(req.Name, scope);

        if (e is null)
        {
            if (others.Count > 0)
            {
                var candidates = others.Select(o => ToItem(o, scope)).ToList();
                var f = new StringBuilder($"Lookup: items named like \"{req.Name}\"\n");
                AppendItems(f, candidates);
                return new LookupResult
                {
                    Request = req,
                    ScopePath = scope,
                    Answer = $"Several items match \"{req.Name}\": {ChatText.Join(candidates.Take(5).Select(c => c.Name).ToList())}. Ask about one of them by its full name, or open it from the list below.",
                    Items = candidates,
                    FactsText = f.ToString(),
                    Description = $"items named like \"{req.Name}\"",
                };
            }
            return Empty(req, scope, $"\"{req.Name}\"", $"I couldn't find anything called \"{req.Name}\" in this scan.");
        }

        var a = e.Analysis;
        var p = Profiler.Build(e);
        var items = new List<ChatItem> { ToItem(e, scope) };
        var lines = new List<string>();
        var paras = new List<string>();
        var s = new StringBuilder();

        // 1. What it is, and what it's part of.
        string what = e.IsDirectory
            ? $"a folder holding {ChatText.Count(e.FileCount, "file", "files")}{(e.DirectoryCount > 0 ? $" and {ChatText.Count(e.DirectoryCount, "subfolder", "subfolders")}" : "")} ({ChatText.Size(e.TotalSize)})"
            : $"{Article(Lower(a?.TypeLabel ?? "file"))} ({ChatText.Size(e.Size)})";
        if (e.Depth == 0)
            what = "the folder this scan is about";
        s.Append($"{e.Name} is {what}");
        lines.Add($"Item: {e.Name} ({(e.IsDirectory ? "folder" : "file")})");
        lines.Add($"What it is: {what}");
        lines.Add($"Location: {e.Path}");
        if (a?.OwnerAppName is { } owner && !owner.Equals(e.Name, StringComparison.OrdinalIgnoreCase))
        {
            s.Append($", part of {owner}");
            lines.Add($"Belongs to: {owner}");
        }
        else if (kb.GetRule(a?.LocationRuleId) is { } rule && e.Depth > 0 &&
                 !string.Equals(rule.Name, ChatText.FolderName(db.GetRoot()?.Path ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            s.Append($", in {rule.Name}");
            lines.Add($"Known location: {rule.Name}: {rule.Description}");
        }
        s.Append('.');
        if (!e.IsDirectory && p.Parent is { Depth: > 0 } parent)
        {
            s.Append(parent.FileCount == 1 ? $" It's the only file in the folder {parent.Name}." : $" It's in the folder {parent.Name}.");
            lines.Add(parent.FileCount == 1 ? $"The only file in the folder {parent.Name}" : $"In the folder {parent.Name}");
        }
        if (p.Program is { } pe && (pe.ProductName ?? pe.FileDescription) is { Length: > 0 } product)
        {
            string desc = pe.FileDescription is { Length: > 0 } fd && !fd.Trim().Equals(product.Trim(), StringComparison.OrdinalIgnoreCase) ? $" ({fd.Trim()})" : "";
            string? version = pe.ProductVersion ?? pe.FileVersion;
            string ver = version is { Length: > 0 } ? $" {version.Trim()}" : "";
            string by = pe.CompanyName is { Length: > 0 } co ? $" by {co.Trim()}" : "";
            s.Append($" The program inside identifies itself as {product.Trim()}{ver}{by}{desc}.");
            lines.Add($"Program details: {product.Trim()}{ver}{by}{desc}");
        }
        if (p.Mod is { } mod)
        {
            s.Append($" It's a mod downloaded from Nexus Mods ({mod.Name}, version {mod.Version}).");
            lines.Add($"Mod download (Nexus Mods): {ItemProfiler.ModText(mod)}");
        }
        Recommendation? containerRec = null;
        if (p.PartOf is { } part)
        {
            var c = part.Entry;
            containerRec = c.Analysis is not null ? RecommendationExplainer.Explain(db, kb, c) : null;
            string cv = containerRec is not null ? $", marked {ChatText.Verdict(containerRec.Verdict, c.Analysis!.Facts)}" : "";
            s.Append($" It's part of {c.Name}, {part.What}{cv}.");
            lines.Add($"Part of: {c.Name} ({c.Path}): {part.What}{(part.Detail is { } d2 ? $": {d2}" : "")}{cv}");
            items.Add(ToItem(c, scope));
        }
        paras.Add(s.ToString());

        // 2. What's inside (folders): one line per kind of file, so it's easy to scan.
        if (p.Kinds.Count > 0)
        {
            s.Clear();
            // "game video (Bink, .bik)", "Game Boy Advance ROM (.gba)"
            static string Kind(KindOfFiles k) =>
                k.Extension.Length == 0 ? Sentence(Lower(k.Label))
                : k.Label.EndsWith(')') ? $"{Sentence(Lower(k.Label))[..^1]}, {k.Extension})"
                : $"{Sentence(Lower(k.Label))} ({k.Extension})";
            static string Examples(KindOfFiles k, int n) => k.Examples.Count == 0 ? "" : (k.Files == 1 ? ": " : ", e.g. ") + string.Join(", ", k.Examples.Take(n));
            s.Append("Inside:");
            foreach (var k in p.Kinds.Take(4))
                s.Append($"\n• {Kind(k)}: {ChatText.Count(k.Files, "file", "files")}, {ChatText.Size(k.Bytes)}{Examples(k, p.Kinds.Count == 1 ? 3 : 2)}");
            if (p.Kinds.Count > 4) s.Append($"\n• …and {ChatText.Count(p.Kinds.Count - 4, "other kind", "other kinds")} of file");
            if (p.Subfolders.Count == 1 && p.Subfolders[0].TotalSize >= e.TotalSize * 0.9)
                s.Append($"\n{(p.Subfolders[0].TotalSize == e.TotalSize ? "All" : "Nearly all")} of it is in the folder {p.Subfolders[0].Name}.");
            else if (p.Subfolders.Count > 0)
                s.Append($"\nBiggest folders: {string.Join(", ", p.Subfolders.Take(3).Select(f => $"{f.Name} ({ChatText.Size(f.TotalSize)})"))}.");
            paras.Add(s.ToString());
            foreach (var k in p.Kinds)
                lines.Add($"Inside: {Kind(k)}: {ChatText.Count(k.Files, "file", "files")}, {ChatText.Size(k.Bytes)}{(k.Examples.Count > 0 ? "; for example " + string.Join(", ", k.Examples) : "")}");
            if (p.Subfolders.Count > 0)
                lines.Add("Folders inside: " + string.Join(", ", p.Subfolders.Select(f => $"{f.Name} ({ChatText.Size(f.TotalSize)}, {ChatText.Count(f.FileCount, "file", "files")})")));
        }

        // 3. What a readme next to it says.
        if (p.Readme is { } readme)
        {
            paras.Add($"Its {readme.Name}{(readme.Folder == e.Name ? "" : $" (in {readme.Folder})")} says: “{readme.Text}”");
            lines.Add($"Text of {readme.Name} (in {readme.Folder}): {readme.Text}");
        }

        // 4. The recommendation: the label, the main reason, and only the reasons that add something.
        s.Clear();
        var own = a is not null && e.Depth > 0 ? RecommendationExplainer.Explain(db, kb, e) : null;
        bool inheritsContainer = containerRec is not null && p.PartOf is not null &&
                                 containerRec.Verdict is Verdict.Review or Verdict.SafeToDelete or Verdict.RecommendDelete &&
                                 (own is null || own.Verdict is Verdict.Keep or Verdict.KeepPersonal);
        var rec = inheritsContainer ? containerRec : own;
        if (rec is not null)
        {
            var recFacts = inheritsContainer ? p.PartOf!.Entry.Analysis!.Facts : a!.Facts;
            string verdict = ChatText.Verdict(rec.Verdict, recFacts);
            string forWhat = inheritsContainer ? $", for all of {p.PartOf!.Entry.Name}" : "";
            s.Append($"Recommendation: {verdict}{forWhat}. {Sentence(rec.Headline.TrimEnd('.'))}.");
            var reasons = UsefulReasons(rec).ToList();
            foreach (var reason in reasons.Take(4)) s.Append($"\n• {reason}.");
            string todo = rec.IsDeletable || rec.Verdict == Verdict.Review ? ActionText(rec.Action) : "";
            if (todo.Length > 0) s.Append('\n').Append(todo);
            // For the AI: one recommendation only (the one that applies), so it can't mix two up.
            lines.Add($"Recommendation: {verdict}{(inheritsContainer ? $", made for the whole of {p.PartOf!.Entry.Name}, which {e.Name} is part of" : "")}: {rec.Headline}");
            lines.AddRange(reasons.Select(r => "Reason: " + r));
            if (todo.Length > 0) lines.Add("What to do: " + todo);
        }
        else if (e.Depth > 0)
        {
            s.Append("There's no recommendation for it (its files weren't checked in this scan).");
        }
        if (e.Depth == 0)
        {
            // The scanned folder itself: what can be freed in it, as on the Overview tab.
            var overview = Overview(req, e.Path);
            paras.Add(overview.Answer);
            lines.Add(overview.FactsText.Trim());
        }
        if (e.IsDirectory && e.Depth > 0 && a is not null && (a.ReclaimBytes > 0 || a.ReviewBytes > 0))
        {
            var inside = new List<string>();
            if (a.ReclaimBytes > 0) inside.Add($"{ChatText.Size(a.ReclaimBytes)} can be deleted");
            if (a.ReviewBytes > 0) inside.Add($"{ChatText.Size(a.ReviewBytes)} is worth reviewing");
            string text = $"Inside it, {ChatText.Join(inside)}.";
            s.Append(s.Length > 0 ? "\n" : "").Append(text);
            lines.Add(text);
        }
        if (a is { Protection: > Protection.None })
            lines.Add($"Protection: {ReportData.ProtectionName(a.Protection)}");
        int recommendationAt = -1;
        if (s.Length > 0) { recommendationAt = paras.Count; paras.Add(s.ToString()); }

        // 5. History: opened, changed (or arrived, when the file's own date is a placeholder), copies.
        s.Clear();
        var usedAt = a?.LastUsedUtc ?? a?.ContainedLastUsedUtc;
        s.Append(usedAt is { } u
            ? $"Last opened {ChatText.Ago(u, _now)}{(a?.UseCount > 0 ? $" ({ChatText.Count(a.UseCount, "time", "times")} recorded)" : "")}"
            : "Never opened, as far as Windows' records go");
        lines.Add($"Last opened: {(usedAt is { } u2 ? ChatText.Ago(u2, _now) : "no record")}");
        var changedAt = a?.ContainedLastModifiedUtc ?? e.ModifiedUtc;
        bool arrived = e.CreatedUtc != default && e.CreatedUtc <= _now;
        if (!e.IsDirectory && FileDates.IsTorrentZipDate(e.ModifiedUtc))
        {
            if (arrived) s.Append($"; added to this PC {ChatText.Ago(e.CreatedUtc, _now)}");
            s.Append(". Its file date, 24 Dec 1996, is the fixed date TorrentZIP gives ROM and emulator files, so it doesn't show when the file was made or changed.");
            lines.Add("File date: 24 Dec 1996 23:32, the fixed TorrentZIP date used on ROM sets (not a real change date)");
            if (arrived) lines.Add($"Added to this PC: {ChatText.Ago(e.CreatedUtc, _now)}");
        }
        else
        {
            if (changedAt != default) s.Append($"; last changed {ChatText.Ago(changedAt, _now)}");
            s.Append('.');
            if (changedAt != default) lines.Add($"Last changed: {ChatText.Ago(changedAt, _now)}");
            if (arrived) lines.Add($"Added to this PC: {ChatText.Ago(e.CreatedUtc, _now)}");
        }

        if (db.GetDuplicateInfo(e.Id) is { } dup)
        {
            string d = dup.IsKeeper
                ? $"It has {ChatText.Count(dup.Copies - 1, "identical copy", "identical copies")} elsewhere; this is the one to keep."
                : $"It's one of {dup.Copies} identical copies; the one to keep is {dup.KeeperPath}.";
            s.Append(' ').Append(d);
            lines.Add(d);
        }
        if (others.Count > 0)
        {
            string more = $"{ChatText.Count(others.Count, "other item has", "other items have")} the same name.";
            s.Append(' ').Append(more);
            lines.Add(more);
        }
        if (e.Depth > 0 || others.Count > 0) paras.Add(s.ToString());
        // "Can I delete it?": the recommendation first, then what it is.
        if (!req.Explain && recommendationAt > 0)
        {
            string first = paras[recommendationAt];
            paras.RemoveAt(recommendationAt);
            paras.Insert(0, first);
        }

        // The biggest things inside come after the item itself, so the list shows what the answer talks about.
        if (e.IsDirectory)
            items.AddRange(db.GetChildren(e.Id, 12).Where(c => c.TotalSize > 0).Take(8).Select(c => ToItem(c, scope)));
        items.AddRange(others.Select(o => ToItem(o, scope)));

        return new LookupResult
        {
            Request = req,
            ScopePath = scope,
            Answer = string.Join("\n\n", paras),
            Items = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList(),
            FactsText = $"Lookup: details of {e.Name}\n{string.Join("\n", lines)}\n",
            Description = $"details of {e.Name}",
        };
    }

    private static string Sentence(string text) => ChatText.Sentence(text);

    /// <summary>The reasons worth showing: not the ones that only repeat the headline ("Your own file" / "It looks like
    /// your own file") or say something general about how recommendations work.</summary>
    private static IEnumerable<string> UsefulReasons(Recommendation rec)
    {
        static string Words(string t) => System.Text.RegularExpressions.Regex.Replace(t.ToLowerInvariant(),
            @"\b(it|this|that|looks|look|seems|like|is|a|an|the|appears|to|be)\b|[^a-z0-9 ]", " ").Trim();
        string headline = System.Text.RegularExpressions.Regex.Replace(Words(rec.Headline), @"\s+", " ");
        foreach (var ev in rec.Evidence)
        {
            string text = ev.Text.Trim().TrimEnd('.');
            if (text.Length == 0) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"never suggests deleting|on its own\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            if (System.Text.RegularExpressions.Regex.Replace(Words(text), @"\s+", " ") == headline) continue;
            yield return text.Replace("PuterScanny ", "", StringComparison.Ordinal);
        }
    }

    /// <summary>"Game video (Bink)" → "game video (Bink)", but "PDF document" stays.</summary>
    private static string Lower(string label)
    {
        var words = label.Split(' ');
        return words.Length >= 2 && GenericWords.Contains(words[0]) && (char.IsLower(words[1][0]) || words[1][0] == '(')
            ? char.ToLowerInvariant(label[0]) + label[1..]
            : label;
    }

    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "Game", "Text", "Disc", "Disk", "Video", "Audio", "Image", "Archive", "Font", "File", "Folder", "Log", "Temporary", "Backup",
        "Shortcut", "Document", "Database", "Configuration", "Settings", "Data", "Cache", "Crash", "Partial", "Compressed", "Unidentified",
        "Installer", "Program", "Library", "Script", "Source", "Web", "Vector", "Raw", "Virtual", "Spreadsheet", "Presentation", "Music",
    };

    private static string Article(string label) =>
        label.Length > 0 && "aeiouAEIOU".Contains(label[0]) ? "an " + label : "a " + label;

    private static string ActionText(CleanupAction a) => a switch
    {
        CleanupAction.Delete => "To remove it, put it in the clean-up basket (Clean up tab); it goes to the Recycle Bin or quarantine, so it can be undone.",
        CleanupAction.Uninstall => "To remove it, uninstall the program from Settings > Apps.",
        CleanupAction.UseWindowsTool => "Free this space with the Windows tool named in its details instead of deleting it by hand.",
        CleanupAction.KeepOneCopy => "Keep one copy and remove the others.",
        CleanupAction.ClearInApp => "Clear it from inside the app that manages it.",
        _ => "",
    };

    // ---------------------------------------------------------------- duplicates

    private LookupResult Duplicates(LookupRequest req, string scope)
    {
        var (sets, copies, bytes) = db.GetDuplicateStats(scope);
        string name = ChatText.FolderName(scope);
        if (sets == 0)
            return Empty(req, scope, $"duplicates in {name}", $"There are no identical copies of files in {name}. (Files are compared byte for byte; very small files are skipped.)");

        var items = new List<ChatItem>();
        foreach (var e in db.GetDuplicateCopies(scope, req.Count))
        {
            var dup = db.GetDuplicateInfo(e.Id);
            items.Add(ToItem(e, scope, dup is null ? "" : $"copy of {ChatText.Relative(dup.Value.KeeperPath, scope)}"));
        }
        string answer =
            $"{name} has {ChatText.Count(sets, "set", "sets")} of identical files. The extra copies ({ChatText.Count(copies, "file", "files")}) take {ChatText.Size(bytes)}." +
            (items.Count > 0 ? $" The biggest extra copies are {ChatText.Join(items.Take(3).Select(i => $"{i.Name} ({i.SizeText})").ToList())}." : "") +
            " In each set, one copy is kept and only the others are offered for removal.";
        var facts = new StringBuilder($"Lookup: duplicates in {scope}\nSets: {sets:N0}; extra copies: {copies:N0} files, {ChatText.Size(bytes)}\nBiggest extra copies:\n");
        AppendItems(facts, items);
        return new LookupResult { Request = req, ScopePath = scope, Answer = answer, Items = items, FactsText = facts.ToString(), Description = $"duplicates in {name}" };
    }

    // ---------------------------------------------------------------- programs

    private LookupResult Programs(LookupRequest req, string scope)
    {
        string root = db.GetRoot()?.Path ?? scope;
        bool inFolder = !string.Equals(root.TrimEnd('\\'), scope.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string prefix = scope.TrimEnd('\\') + "\\";

        var apps = db.GetApps()
            .Where(a => !a.IsSystemComponent && !a.IsFramework)
            .Where(a => !inFolder || (a.InstallLocation is { } loc && (loc.TrimEnd('\\') + "\\").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        string where = inFolder ? $" in {ChatText.FolderName(scope)}" : " on this PC";
        string what;
        int noRecord = 0;
        static long SizeOf(InstalledApp a) => a.MeasuredFiles > 0 ? a.MeasuredBytes : a.EstimatedSizeBytes ?? 0;
        if (req.Filter == ListFilter.Delete)
        {
            apps = apps.Where(a => a.UninstallConfidence > 0).ToList();
            what = $"programs{where} worth uninstalling";
        }
        else if (req.UnusedDays is int days)
        {
            var since = _now.AddDays(-days);
            noRecord = apps.Count(a => a.LastUsedUtc is null);
            apps = apps.Where(a => a.LastUsedUtc < since).ToList();
            what = $"programs{where} not used in {ChatText.Period(days)}";
        }
        else
        {
            what = $"programs{where}";
        }
        if (req.Name is { Length: > 0 } n)
        {
            apps = apps.Where(a => a.Name.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
            what += $" with \"{n}\" in the name";
        }
        apps = req.Sort switch
        {
            EntrySort.Oldest or EntrySort.LeastRecentlyUsed => apps.OrderBy(a => a.LastUsedUtc ?? DateTime.MinValue).ThenByDescending(a => a.MeasuredBytes).ToList(),
            EntrySort.Newest => apps.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue).ToList(),
            _ => apps.OrderByDescending(SizeOf).ToList(),
        };

        var items = apps.Take(req.Count).Select(a => new ChatItem
        {
            Name = a.Name,
            Path = a.InstallLocation ?? "",
            IsFolder = true,
            Bytes = SizeOf(a),
            SizeText = a.MeasuredFiles > 0 ? ChatText.Size(a.MeasuredBytes)
                : a.EstimatedSizeBytes is long est and > 0 ? $"about {ChatText.Size(est)}" : "size unknown",
            VerdictText = a.UninstallConfidence > 0 ? "Uninstall?" : "",
            Action = a.UninstallConfidence > 0 ? CleanupAction.Uninstall : CleanupAction.None,
            Headline = a.UninstallReason ?? "",
            Note = (a.LastUsedUtc is { } lu ? $"last used {ChatText.Ago(lu, _now)}" : "no record of use") +
                   (a.UseCount > 0 ? $" · used {a.UseCount:N0} times" : "") + (a.Publisher is { } p ? $" · {p}" : ""),
        }).ToList();

        string answer;
        if (items.Count == 0)
        {
            answer = $"There are no {what}.";
        }
        else
        {
            string list = ChatText.Join(items.Take(3).Select(i => $"{i.Name} ({i.SizeText}, {i.Note.Split(" · ")[0]})").ToList());
            string howMany = ChatText.Count(apps.Count, "program", "programs");
            answer = req.Filter == ListFilter.Delete
                ? $"{Sentence(howMany)}{where} {(apps.Count == 1 ? "is" : "are")} worth uninstalling: {list}."
                : req.UnusedDays is int unusedDays
                    ? $"{howMany}{where} {(apps.Count == 1 ? "hasn't" : "haven't")} been used in {ChatText.Period(unusedDays)}, as far as Windows' records show: {list}."
                    : $"There are {howMany}{where}. {(req.Sort == EntrySort.Size ? "The biggest" : "First")}: {list}.";
            if (req.Filter == ListFilter.Delete) answer += " Programs are removed by uninstalling them from Settings > Apps, never by deleting their files.";
        }
        if (req.UnusedDays is not null && noRecord > 0)
            answer += $" {ChatText.Count(noRecord, "other program has", "other programs have")} no usage record at all, so there's no telling when {(noRecord == 1 ? "it was" : "they were")} last used (some programs never leave one).";
        var facts = new StringBuilder($"Lookup: {what}\nMatching: {apps.Count:N0} programs\n");
        AppendItems(facts, items);
        return new LookupResult { Request = req, ScopePath = scope, Answer = answer, Items = items, FactsText = facts.ToString(), Description = what };
    }

    // ---------------------------------------------------------------- kinds of files

    private LookupResult Types(LookupRequest req, string scope)
    {
        var stats = db.GetCategoryStats(scope).Where(s => s.Bytes > 0).ToList();
        string name = ChatText.FolderName(scope);
        if (stats.Count == 0) return Empty(req, scope, $"kinds of files in {name}", $"There are no files in {name}.");
        long total = Math.Max(1, stats.Sum(s => s.Bytes));
        var rows = stats.Take(Math.Max(8, req.Count)).Select(s =>
            $"{kb.CategoryName(s.Category)}: {ChatText.Size(s.Bytes)} ({ChatText.Count(s.Files, "file", "files")}, {(s.Bytes * 100.0 / total).ToString("0", CultureInfo.CurrentCulture)}%)").ToList();
        string answer = $"By kind of file, {name} is mostly {ChatText.Join(rows.Take(4).ToList())}.";
        return new LookupResult
        {
            Request = req,
            ScopePath = scope,
            Answer = answer,
            FactsText = $"Lookup: kinds of files in {scope}\n{string.Join("\n", rows)}\n",
            Description = $"kinds of files in {name}",
        };
    }

    // ---------------------------------------------------------------- helpers

    private static LookupResult Empty(LookupRequest req, string scope, string description, string answer) => new()
    {
        Request = req,
        ScopePath = scope,
        Answer = answer,
        FactsText = $"Lookup: {description}\n{answer}\n",
        Description = description,
    };

    public ChatItem ToItem(FileSystemEntry e, string scope, string note = "")
    {
        var a = e.Analysis;
        return new ChatItem
        {
            EntryId = e.Id,
            Name = e.Depth == 0 ? e.Path : e.Name,
            Path = e.Path,
            IsFolder = e.IsDirectory,
            Bytes = e.TotalSize,
            SizeText = ChatText.Size(e.TotalSize),
            Verdict = a?.Verdict,
            VerdictText = a?.Verdict is { } v ? ChatText.Verdict(v, a.Facts) : "",
            Action = a?.Action ?? CleanupAction.None,
            Headline = a?.Headline ?? "",
            Note = note.Length > 0 ? note : (e.IsDirectory ? ChatText.Count(e.FileCount, "file", "files") : a?.TypeLabel ?? ""),
        };
    }

    private static void AppendItems(StringBuilder sb, IReadOnlyList<ChatItem> items)
    {
        int i = 1;
        foreach (var it in items)
        {
            sb.Append(i++.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(it.Name)
              .Append(it.IsFolder ? " (folder)" : "").Append(" — ").Append(it.SizeText);
            if (it.VerdictText.Length > 0)
            {
                sb.Append(" — marked: ").Append(it.VerdictText);
                if (it.Headline.Length > 0) sb.Append(" (").Append(it.Headline.TrimEnd('.')).Append(')');
            }
            if (it.Note.Length > 0) sb.Append(" — ").Append(it.Note);
            if (it.Path.Length > 0) sb.Append(" — ").Append(it.Path);
            sb.Append('\n');
        }
    }
}
