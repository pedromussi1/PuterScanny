using System.Globalization;
using System.Text.RegularExpressions;
using PuterScanny.Core.Models;
using PuterScanny.Storage;

namespace PuterScanny.Chat;

/// <summary>
/// Recognises common questions ("what can I delete in Downloads?", "biggest videos I haven't watched in a year",
/// "what is retip?") and turns them into an exact lookup, instantly and without any AI. Questions it doesn't
/// recognise return null and can go to the local AI instead.
/// </summary>
public sealed partial class QuestionMatcher(ScanLookup lookup)
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Words that never name a file or folder by themselves.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "this", "that", "these", "those", "them", "here", "there", "this one", "that one", "this folder", "the folder",
        "anything", "something", "everything", "stuff", "things", "files", "folders", "all", "all of it", "it all", "some",
        "what", "which", "space", "the space", "most space", "the most space", "more space", "this drive", "my files", "my stuff",
        "everything here", "anything here", "something here", "any of it", "any of them", "some of it", "some of them",
    };

    private static readonly (string Pattern, string Category)[] CategoryWords =
    [
        (@"videos?|movies?|films?|clips?|recordings?", "video"),
        (@"photos?|pictures?|pics|images?|screenshots?", "image"),
        (@"music|songs?|audio|mp3s?", "audio"),
        (@"documents?|docs|pdfs?|spreadsheets?|presentations?", "document"),
        (@"installers?|setup files?|setups", "installer"),
        (@"archives?|zips?|zip files?|rars?|compressed files?", "archive"),
        (@"isos?|disk images?|disc images?", "disk-image"),
        (@"logs?|log files?", "log"),
        (@"crash dumps?|dumps?|memory dumps?", "crash-dump"),
        (@"backups?", "backup"),
        (@"temp files?|temporary files?", "temporary"),
        (@"cache files?|caches?", "cache"),
        (@"fonts?", "font"),
        (@"e-?books?", "ebook"),
        (@"virtual machines?|vms?|virtual disks?", "virtual-machine"),
        (@"source code|code files?", "code"),
        (@"exes?|executables?|programs? files?", "executable"),
        (@"databases?", "database"),
    ];

    public LookupRequest? Match(string question, string scopePath, LookupRequest? previous = null)
    {
        string q = Regex.Replace(question.Trim(), @"\s+", " ").TrimEnd('?', '!', '.', ' ');
        if (q.Length == 0) return null;

        // 1. Where: an explicit path, or "in/inside <folder name>" that exists in the scan.
        string? folder = null;
        string rest = q;
        var path = PathRx().Match(q);
        if (path.Success && lookup.Db.GetEntryByPath(path.Value.TrimEnd(' ', '\\')) is { IsDirectory: true } byPath)
        {
            folder = byPath.Path;
            rest = q.Remove(path.Index, path.Length);
        }
        else if (FindFolderPhrase(q, scopePath) is { } phrase)
        {
            folder = phrase.Path;
            rest = q.Remove(phrase.Index, phrase.Length);
        }
        bool thisFolder = Regex.IsMatch(rest, @"\b(this|the|current) (folder|directory|place)\b|\b(in )?here\b", Opts);
        rest = Regex.Replace(rest, @"\b(this|the|my|current) (folder|drive|directory|place)\b", "", Opts);
        rest = Regex.Replace(rest, @"\s+", " ").Trim();
        string low = rest.ToLowerInvariant();

        // "what are these files?", "what's in here?", "list the files in SNES Games": describe the folder and what's in it.
        if (ContentsRx().IsMatch(low) || ContentsRx().IsMatch(Regex.Replace(low, @"\s*\bhere\b", "").Trim()))
            return new LookupRequest { Kind = LookupKind.Item, Name = folder, Explain = true };

        // "what is this folder?", "tell me about Downloads", "what is it for?": the folder itself (or the item asked about just before).
        if (DescribeRx().IsMatch(low) && (folder is not null || thisFolder || Regex.IsMatch(low, @"\b(it|this)\b")))
        {
            if (folder is null && !thisFolder && previous is { Kind: LookupKind.Item })
                return previous with { Explain = true };
            return new LookupRequest { Kind = LookupKind.Item, Name = folder, Explain = true };
        }
        // "can I delete this folder?", "is it safe to delete?", "why is this folder marked Review?"
        if ((folder is not null || thisFolder) && SelfVerdictRx().IsMatch(low))
            return new LookupRequest { Kind = LookupKind.Item, Name = folder };

        // 2. Modifiers.
        long? minBytes = MinSize(low);
        int? days = Period(low);
        int? count = Count(low);
        string? category = Category(low);
        EntryKind kind = Regex.IsMatch(low, @"\b(folders|directories|folder|directory)\b") ? EntryKind.Folder
            : Regex.IsMatch(low, @"\bfiles?\b") ? EntryKind.File : EntryKind.Any;
        EntrySort? sort = Regex.IsMatch(low, @"\boldest\b") ? EntrySort.Oldest
            : Regex.IsMatch(low, @"\b(newest|latest|most recent|recently (added|changed|downloaded))\b") ? EntrySort.Newest
            : Regex.IsMatch(low, @"\bleast (recently )?used\b") ? EntrySort.LeastRecentlyUsed
            : null;
        bool unused = UnusedRx().IsMatch(low);
        bool older = Regex.IsMatch(low, @"\b(older than|not (been )?(changed|modified|updated)|haven'?t (changed|modified|updated))\b");

        bool followUp = previous is not null && FollowUpRx().IsMatch(low);

        if (Regex.IsMatch(low, @"^(hi|hello|hey|thanks|thank you|ok|okay|help|\?)$"))
            return null;
        // "summary of Downloads", "how big is Downloads?"
        if (folder is not null && Regex.IsMatch(low, @"^(what about|how about|summari[sz]e|summary( of)?|overview( of)?|how big is|how large is|how much space (is|does)( it)?( take| use)?|how much is)$"))
            return new LookupRequest { Kind = LookupKind.Overview, Folder = folder };

        // 3. What is being asked.
        LookupRequest? request = ItemQuestion(rest, folder ?? scopePath, previous) ?? Intent(low, unused, days);

        bool modifiers = category is not null || minBytes is not null || unused || older || sort is not null || Regex.IsMatch(low, @"\bold\b");
        if (request is null && modifiers)
            request = followUp && previous!.Kind is LookupKind.List or LookupKind.Programs ? previous with { } : new LookupRequest { Kind = LookupKind.List };
        if (request is null && followUp)
            request = previous! with { };
        if (request is null)
        {
            // A bare name: "retip-1.15-win-amd64".
            if (folder is null && LooksLikeName(q) && lookup.FindItem(q, scopePath).Entry is not null)
                return new LookupRequest { Kind = LookupKind.Item, Name = q };
            // Only a folder: "Downloads" or "what about Downloads?".
            if (folder is not null && low.Trim().Length <= 12)
                return new LookupRequest { Kind = previous?.Kind == LookupKind.Item ? LookupKind.Overview : previous?.Kind ?? LookupKind.Overview, Folder = folder };
            return null;
        }

        if (followUp && previous is not null)
        {
            folder ??= previous.Folder;
            if (request.Kind == previous.Kind && request.Kind is LookupKind.List or LookupKind.Programs && !ReferenceEquals(request, previous))
            {
                // "and which of those are videos?": narrow the previous list.
                request = previous with
                {
                    Filter = request.Filter != ListFilter.Any ? request.Filter : previous.Filter,
                    UnusedDays = request.UnusedDays ?? previous.UnusedDays,
                };
            }
        }

        // 4. Apply the modifiers.
        if (request.Kind is LookupKind.List or LookupKind.Programs)
        {
            request = request with
            {
                Category = category ?? request.Category,
                MinBytes = minBytes ?? request.MinBytes,
                ItemKind = category is not null ? EntryKind.File : kind != EntryKind.Any ? kind : request.ItemKind,
                Sort = sort ?? request.Sort,
                UnusedDays = unused ? days ?? request.UnusedDays ?? 365 : request.UnusedDays,
                OlderDays = older && !unused ? days ?? 365 : request.OlderDays,
            };
            if (sort is null && !unused && !older && Regex.IsMatch(low, @"\bold\b"))
                request = request with { Sort = EntrySort.Oldest };
        }
        if (count is int n) request = request with { Count = n };
        if (folder is not null) request = request with { Folder = folder };
        return request;
    }

    // ---------------------------------------------------------------- intents

    private static LookupRequest? Intent(string low, bool unused, int? days)
    {
        if (Regex.IsMatch(low, @"\b(duplicates?|duplicated|identical|same files?|copies of|double copies|twice)\b"))
            return new LookupRequest { Kind = LookupKind.Duplicates };

        if (Regex.IsMatch(low, @"\b(mods?|nexus)\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.Mods };
        if (Regex.IsMatch(low, @"\b(leftovers?|left ?over|left behind|remnants?|traces of)\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.Leftovers };
        if (Regex.IsMatch(low, @"\bempty folders?\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.EmptyFolders };
        if (Regex.IsMatch(low, @"\b(broken|dead) (shortcuts?|links?)\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.BrokenShortcuts };
        if (Regex.IsMatch(low, @"\b(unfinished|partial|incomplete|failed|interrupted) downloads?\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.PartialDownloads };

        bool programs = Regex.IsMatch(low, @"\b(programs?|apps?|applications?|software|games?)\b") &&
                        !Regex.IsMatch(low, @"\b(program|app|game) (files?|saves?|clips?|recordings?|data|folders?)\b|\bfiles\b");
        if (programs)
        {
            bool uninstall = Regex.IsMatch(low, @"\b(uninstall|remove|delete|get rid of|don'?t need)\b");
            return new LookupRequest
            {
                Kind = LookupKind.Programs,
                Filter = uninstall && !unused ? ListFilter.Delete : ListFilter.Any,
                UnusedDays = unused ? days ?? 365 : null,
                Sort = unused && !Regex.IsMatch(low, @"\b(biggest|largest|most space)\b") ? EntrySort.LeastRecentlyUsed : EntrySort.Size,
            };
        }

        if (Regex.IsMatch(low, @"\b(shouldn'?t|should not|must not|mustn'?t|can'?t|cannot|don'?t|do not|never) (i )?(delete|remove|touch)\b|\b(must|should|need to) keep\b|\b(must|should|can) (i |we )?not (delete|remove|touch)\b|\bimportant\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.Keep };
        if (Regex.IsMatch(low, @"\b(required|needed by windows|windows needs|system files?|essential|critical)\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.Required };
        if (Regex.IsMatch(low, @"\bno reason\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.NoReason };
        if (Regex.IsMatch(low, @"\b(unknown|unidentified|not identified|identif\w*|don'?t know what|no ?one knows|nobody knows|mystery|mysterious|unrecogni[sz]ed|unexplained)\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.Unidentified };
        if (Regex.IsMatch(low, @"\b(delete|deletable|deleted|remove|removable|clean( ?up)?|cleaning|free( up)?|freed|reclaim|get rid|junk|clutter|trash|wasted?|waste of space|safe to)\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.Delete };
        if (Regex.IsMatch(low, @"\b(review|reviewing|not sure|unsure|decide|check (it )?myself|look at myself)\b"))
            return new LookupRequest { Kind = LookupKind.List, Filter = ListFilter.Review };
        if (unused)
            return new LookupRequest { Kind = LookupKind.List, UnusedDays = days ?? 365 };
        if (Regex.IsMatch(low, @"\b(kinds?|types?|sorts?|categories|breakdown|made (up )?of)\b"))
            return new LookupRequest { Kind = LookupKind.Types };
        if (Regex.IsMatch(low, @"\b(biggest|largest|huge|heaviest|bulkiest|big|large|most space|taking up|takes up|take up|using (the most |most |a lot of )?space|space hogs?|hogging|eating)\b"))
            return new LookupRequest { Kind = LookupKind.List };
        if (Regex.IsMatch(low, @"\b(summary|summari[sz]e|overview|how much space|how big|how large|how full|total|what'?s (here|in)|what is (here|in)|status|tell me about|describe)\b") ||
            Regex.IsMatch(low, @"^(what('?s| is) here|what do (i|we) have)$"))
            return new LookupRequest { Kind = LookupKind.Overview };
        return null;
    }

    /// <summary>"what is X", "why is X marked…", "can I delete X", "tell me about X"…, when X is an item in the scan.</summary>
    private LookupRequest? ItemQuestion(string q, string scopePath, LookupRequest? previous)
    {
        foreach (var rx in ItemPatterns)
        {
            var m = rx.Match(q);
            if (!m.Success) continue;
            string name = CleanName(m.Groups["n"].Value);
            if (name.Length == 0 || IsGenericName(name))
            {
                // "can I delete it?" right after asking about an item: the same item.
                if (Regex.IsMatch(name, @"^(it|this|that|this one|that one|this folder|the folder)$", Opts) || name.Length == 0)
                {
                    if (previous is { Kind: LookupKind.Item }) return previous with { Explain = Regex.IsMatch(q, @"^(?:what|what'?s|whats|tell me|explain|describe)\b", Opts) };
                    if (Regex.IsMatch(q, @"\b(this|the) folder\b", Opts) || name is "this" or "it" && Regex.IsMatch(q, @"^(can|should|could|is) ", Opts))
                        return new LookupRequest { Kind = LookupKind.Item, Name = null };
                }
                continue;
            }
            if (name.Split(' ').Length <= 3 && Category(name.ToLowerInvariant()) is { } cat &&
                Regex.IsMatch(name, $@"^(?:all |any |old |big |the |my |these |those )*(?:{CategoryWords.First(c => c.Category == cat).Pattern})$", Opts))
                return new LookupRequest { Kind = LookupKind.List, Category = cat, ItemKind = EntryKind.File };
            var (entry, others) = lookup.FindItem(name, scopePath);
            // Part of a name is fine too ("the minish cap"), as long as it's specific enough to mean something.
            bool specific = LooksLikeName(name) || name.Length >= 4 && !IsGenericName(name);
            bool explain = Regex.IsMatch(q, @"^(?:what|what'?s|whats|tell me|explain|talk|details|info|information|who)\b", Opts);
            if (entry is not null && (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(Path.GetFileNameWithoutExtension(entry.Name), name, StringComparison.OrdinalIgnoreCase) ||
                                      specific))
                return new LookupRequest { Kind = LookupKind.Item, Name = name, Explain = explain };
            if (entry is null && others.Count > 0 && specific)
                return new LookupRequest { Kind = LookupKind.Item, Name = name, Explain = explain };
        }
        return null;
    }

    private static readonly Regex[] ItemPatterns =
    [
        new(@"\b(?:safe|ok|okay|fine|alright) to (?:delete|remove|get rid of|trash) (?<n>.+)$", Opts),
        new(@"^(?:can|should|could|may|do|must) i (?:safely |really |just )?(?:delete|remove|get rid of|trash|keep|uninstall) (?<n>.+)$", Opts),
        new(@"^why (?:is|are|was|were) (?<n>.+?) (?:marked|recommended|flagged|kept|listed|labell?ed|set|considered|shown|in|on|a|an|not|safe|important|needed|required|deletable|under|called)\b", Opts),
        new(@"^why (?:should|shouldn'?t|can'?t|cannot|can) i (?:keep|delete|remove) (?<n>.+)$", Opts),
        new(@"^is (?<n>.+?) (?:safe|ok|okay|fine|needed|required|important|used|in use|necessary|junk|a virus|malware|deletable|removable)\b", Opts),
        new(@"^do i (?:need|use) (?<n>.+)$", Opts),
        new(@"^how (?:much space|big|large) (?:is|are|does|do) (?<n>.+?)(?: take| use| taking| using| take up)?$", Opts),
        new(@"^(?:tell me|explain|talk) (?:more )?(?:about )?(?<n>.+)$", Opts),
        new(@"^(?:what|who) (?:does|uses|made|created|owns|installed|put) (?<n>.+?)(?: do| for| there| here)?$", Opts),
        new(@"^(?:details|info|information) (?:of|for|on|about) (?<n>.+)$", Opts),
        new(@"^(?:what|what'?s|whats) (?:is |are )?(?:the |this |that )?(?:file |folder )?(?<n>.+)$", Opts),
    ];

    private static string CleanName(string s)
    {
        s = s.Trim().Trim('"', '\'', '“', '”', '‘', '’', '`', '?', '.', ' ');
        s = Regex.Replace(s, @"^(the|my|this|that|a|an) (file |folder |program |app )?", "", Opts);
        s = Regex.Replace(s, @" (file|folder|program|app|thing|one)$", "", Opts);
        s = Regex.Replace(s, @" (for|now|then|safely|too|anyway|please)$", "", Opts);
        return s.Trim().Trim('"', '\'', '“', '”', '`');
    }

    private static bool IsGenericName(string s) =>
        Generic.Contains(s) || Regex.IsMatch(s, @"\b(biggest|largest|space|delete|remove|safe|clean|unused|duplicates?|programs?|anything|everything|something|which|files|folders|kinds?|types?)\b", Opts);

    /// <summary>Names with an extension, digits, dashes or underscores are specific enough to search for as they are.</summary>
    private static bool LooksLikeName(string s) =>
        s.Length >= 3 && !IsGenericName(s) && !s.Contains(' ') || Regex.IsMatch(s, @"\.[a-z0-9]{1,5}$|[0-9_\-]", Opts) && !IsGenericName(s);

    // ---------------------------------------------------------------- where

    private (string Path, int Index, int Length)? FindFolderPhrase(string q, string scopePath)
    {
        foreach (Match m in FolderPrepRx().Matches(q))
        {
            string after = q[(m.Index + m.Length)..];
            var words = after.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int take = 0;
            while (take < words.Length && take < 5 && !Regex.IsMatch(words[take], @"^(that|which|i|i'?ve|i'?m|older|bigger|larger|over|under|not|than|with|from|and|or|are|is|was|were|by|for|since|in|inside|any|anymore|anything|can|could|should|do|does|have|haven'?t|more|less|at|to)$", Opts))
                take++;
            for (int n = take; n >= 1; n--)
            {
                string phrase = string.Join(' ', words.Take(n)).Trim('"', '\'', ',', '?');
                string candidate = Regex.Replace(phrase, @"^(the|my|a) ", "", Opts);
                candidate = Regex.Replace(candidate, @" (folder|directory|drive)$", "", Opts);
                if (candidate.Length < 2 || Regex.IsMatch(candidate, @"^(\d+|a|an|one|two|three|six|the last|last|the past|past|total|general|here|there|it|this|that|use|me|my|all)\b", Opts))
                    continue;
                if (lookup.FindFolder(candidate, scopePath) is { } entry)
                {
                    int length = m.Length + after.IndexOf(phrase, StringComparison.Ordinal) + phrase.Length;
                    return (entry.Path, m.Index, Math.Min(length, q.Length - m.Index));
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- modifiers

    private static long? MinSize(string low)
    {
        var m = Regex.Match(low, @"(?:over|more than|bigger than|larger than|above|at least|greater than|>|>=|minimum( of)?)\s*(?<v>\d+(?:[.,]\d+)?)\s*(?<u>kb|mb|gb|tb|k|m|g|t)\b");
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups["v"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        double unit = m.Groups["u"].Value[0] switch { 'k' => 1024d, 'm' => 1024d * 1024, 'g' => 1024d * 1024 * 1024, _ => 1024d * 1024 * 1024 * 1024 };
        return (long)(v * unit);
    }

    private static int? Period(string low)
    {
        var m = Regex.Match(low, @"\b(?<n>\d+|a|an|one|two|three|four|five|six|seven|eight|nine|ten|twelve|a couple of|a few|several)\s+(?<u>day|week|month|year)s?\b");
        if (!m.Success)
        {
            if (Regex.IsMatch(low, @"\b(last|past|this) year\b|\ba year\b|\bayear\b")) return 365;
            if (Regex.IsMatch(low, @"\b(last|past) month\b")) return 30;
            if (Regex.IsMatch(low, @"\b(ages|a long time|forever|years)\b")) return 730;
            return null;
        }
        int n = m.Groups["n"].Value switch
        {
            "a" or "an" or "one" => 1,
            "two" or "a couple of" => 2,
            "three" or "a few" => 3,
            "four" => 4,
            "five" => 5,
            "six" or "several" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "twelve" => 12,
            var d => int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : 1,
        };
        int days = m.Groups["u"].Value switch { "day" => n, "week" => n * 7, "month" => n * 30, _ => n * 365 };
        return Math.Clamp(days, 1, 36500);
    }

    private static int? Count(string low)
    {
        var m = Regex.Match(low, @"\b(?:top|first|show me|list|give me)\s+(?<n>\d{1,2})\b|\b(?<n>\d{1,2})\s+(?:biggest|largest|oldest|newest|items|files|folders|programs|apps|things|videos|pictures|photos|installers)\b");
        return m.Success && int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
            ? Math.Min(n, ScanLookup.MaxCount)
            : null;
    }

    private static string? Category(string low)
    {
        foreach (var (pattern, id) in CategoryWords)
            if (Regex.IsMatch(low, $@"\b(?:{pattern})\b"))
                return id;
        return null;
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^""?*<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathRx();

    [GeneratedRegex(@"\b(?:in|inside|under|within|from|about)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolderPrepRx();

    [GeneratedRegex(@"\b(not used|(?:haven'?t|havent|have not|hasn'?t|has not|didn'?t|did not|don'?t|dont|do not|doesn'?t|does not|never)(?: i| we| you| it| they)?(?: ever| even| really)? (?:use|used|open|opened|touch|touched|look at|looked at|watch|watched|play|played|listen to|listened to|run|ran|need|needed|launch|launched)|unused|not (?:been )?(?:opened|touched|used|watched|played)|untouched|forgotten|stale|abandoned|no longer (?:use|used|need|needed)|not in use)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnusedRx();

    [GeneratedRegex(@"^(and|also|what about|how about|only|just|now|same|then|but)\b|\b(those|these|them|of them|that list|the same|ones)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FollowUpRx();

    /// <summary>Asking what a folder holds (after "this folder" / the folder's name were taken out of the question).</summary>
    [GeneratedRegex(@"^(?:so )?(?:what|which) (?:are|r|is) (?:these|those|the|they|all (?:the|these|of these))(?: (?:files|things|items|folders|files and folders|files here))?(?: in(?:side)?(?: it| there)?)?$" +
                    @"|^what (?:files|things|items|stuff) (?:are|is|do i have)(?: (?:in|inside|there|in it|in there|inside it))?$" +
                    @"|^(?:list|show(?: me)?|give me|tell me) (?:all )?(?:of )?(?:the )?(?:files|contents|items|everything|what'?s (?:in|inside))(?: (?:in|inside)(?: it)?)?$" +
                    @"|^(?:what'?s|whats|what is|what are) (?:in|inside)(?: (?:it|there))?$|^what does (?:it|this) (?:contain|hold|have(?: in it)?)$|^(?:the )?contents$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentsRx();

    /// <summary>Asking what a folder (or the item just discussed) is: "what is this?", "tell me about it", "what is it for?".</summary>
    [GeneratedRegex(@"^(?:so |and |ok |okay )?(?:what|what'?s|whats|what is|tell me(?: more)?(?: about)?|describe|explain|about|info(?:rmation)?(?: on| about)?|details(?: of| on| about)?)(?: (?:it|this|that))?(?: (?:for|used for|about|do|doing here|there for|here for))?$" +
                    @"|^what (?:does|do) (?:it|this) (?:do|mean)$|^what(?:'s| is) (?:the )?(?:purpose|point|use) of(?: it| this)?$|^why is (?:it|this) here$|^where (?:does|did) (?:it|this) come from$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DescribeRx();

    /// <summary>Asking for the recommendation of the folder itself: "can I delete this folder?", "is it safe to delete?".</summary>
    [GeneratedRegex(@"^(?:can|should|could|may|must|do) i (?:safely |really |just )?(?:delete|remove|get rid of|trash|keep|clean up|clean)(?: (?:it|this|everything|all of it|all))?$" +
                    @"|^(?:is|are) (?:(?:it|this|they) )?(?:safe|ok|okay|fine|needed|required|important|junk|necessary|deletable|removable|used|in use)(?: to (?:delete|remove|keep))?$" +
                    @"|^why (?:is|was) (?:it |this )?(?:marked|recommended|flagged|kept|listed|labell?ed|shown|considered|safe|important)\b.*$|^do i (?:need|use)(?: it| this)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfVerdictRx();
}
