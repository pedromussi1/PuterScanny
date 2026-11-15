using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuterScanny.Knowledge;

/// <summary>One thing worth knowing about an item: a placeholder date, a naming convention…</summary>
public sealed record KnownDetail(string Id, string Text);

/// <summary>Something people ask about ("files from a Mac", "code projects"): the entries and file types that make it up,
/// or for folders, the marker files that show what a folder is (.git, package.json…).</summary>
public sealed record KnownTopic(string Id, string Name, Regex Phrases, IReadOnlySet<string> Details, IReadOnlySet<string> Extensions, IReadOnlyList<string> Markers);

/// <summary>
/// Small, well-documented facts about how files get their names and dates (known-details.json): TorrentZIP's fixed
/// 1996 date, No-Intro ROM names, camera file names, browsers' "(1)" copies… so answers explain them instead of
/// taking them literally. Every entry applies only on an exact match.
/// </summary>
public sealed partial class KnownDetails
{
    private sealed record DateRule(string Id, DateTime? Local, DateTime? Utc, DateTime? UtcFrom, DateTime? UtcTo, string Text, string? FolderText);

    private sealed record NameRule(
        string Id, bool Files, bool Folders, HashSet<string>? Extensions, long? MaxBytes, Regex Pattern, string Text, string? FolderText,
        string? Sibling, string? SiblingText, Dictionary<string, Dictionary<string, string>> Lookups, HashSet<string> Suppresses);

    private readonly List<DateRule> _dates;
    private readonly List<NameRule> _names;
    private readonly string _futureDate;

    private KnownDetails(List<DateRule> dates, List<NameRule> names, string futureDate, List<KnownTopic> topics)
    {
        _dates = dates;
        _names = names;
        _futureDate = futureDate;
        Topics = topics;
    }

    public static KnownDetails Empty { get; } = new([], [], "", []);

    public IReadOnlyList<KnownTopic> Topics { get; }

    public KnownTopic? GetTopic(string? id) => Topics.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The topic a question is about ("any files from a Mac?" → mac), or null.</summary>
    public KnownTopic? TopicFor(string question) => Topics.FirstOrDefault(t => t.Phrases.IsMatch(question));

    /// <summary>True when a file or folder belongs to the topic by its name or file type (marker-based topics are found by the scan lookup).</summary>
    public bool IsInTopic(KnownTopic topic, string name, bool isFolder, long size)
    {
        if (!isFolder && topic.Extensions.Count > 0 && topic.Extensions.Contains(Path.GetExtension(name).ToLowerInvariant())) return true;
        return topic.Details.Count > 0 && ForName(name, isFolder, size).Any(d => topic.Details.Contains(d.Id));
    }

    public int Count => _dates.Count + _names.Count;

    public IEnumerable<string> Ids => _dates.Select(d => d.Id).Concat(_names.Select(n => n.Id));

    // ---------------------------------------------------------------- dates

    /// <summary>What a file date really means, when it's a known placeholder (or in the future); null for an ordinary date.</summary>
    public KnownDetail? ForDate(DateTime utc, DateTime nowUtc)
    {
        if (utc == default) return null;
        foreach (var rule in _dates)
            if (Matches(rule, utc)) return new KnownDetail(rule.Id, rule.Text);
        if (_futureDate.Length > 0 && utc > nowUtc.AddDays(2)) return new KnownDetail("future-date", _futureDate);
        return null;
    }

    /// <summary>True when the date says nothing about when the file was made or changed.</summary>
    public bool IsPlaceholderDate(DateTime utc, DateTime nowUtc) => ForDate(utc, nowUtc) is not null;

    /// <summary>The exact moments (UTC ticks) of the fixed placeholder dates, on this PC, for counting files that carry them.</summary>
    public IReadOnlyList<(string Id, long[] Ticks)> ExactPlaceholderTicks()
    {
        var list = new List<(string, long[])>();
        foreach (var rule in _dates)
        {
            var ticks = new List<long>();
            if (rule.Local is { } local) ticks.Add(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local).Ticks);
            if (rule.Utc is { } u) ticks.Add(u.Ticks);
            if (ticks.Count > 0) list.Add((rule.Id, ticks.Distinct().ToArray()));
        }
        return list;
    }

    /// <summary>"12 files here carry TorrentZIP's fixed date…".</summary>
    public string? FolderTextForDate(string id, string count) =>
        _dates.FirstOrDefault(d => d.Id == id)?.FolderText?.Replace("{count}", count, StringComparison.Ordinal);

    private static bool Matches(DateRule rule, DateTime utc)
    {
        if (rule.Local is { } local)
        {
            var l = utc.ToLocalTime();
            if (l.Year == local.Year && l.Month == local.Month && l.Day == local.Day && l.Hour == local.Hour && l.Minute == local.Minute) return true;
        }
        if (rule.Utc is { } u && Math.Abs((utc - u).TotalSeconds) < 60) return true;
        if (rule.UtcFrom is not null || rule.UtcTo is not null)
            return (rule.UtcFrom is not { } from || utc >= from) && (rule.UtcTo is not { } to || utc < to);
        return false;
    }

    // ---------------------------------------------------------------- names

    /// <summary>What an item's name tells, by known conventions. <paramref name="siblingExists"/> checks whether another
    /// item with a given name is in the same folder.</summary>
    public IReadOnlyList<KnownDetail> ForName(string name, bool isFolder, long size, Func<string, bool>? siblingExists = null)
    {
        var found = new List<(NameRule Rule, string Text)>();
        string ext = isFolder ? "" : Path.GetExtension(name).ToLowerInvariant();
        foreach (var rule in _names)
        {
            if (isFolder ? !rule.Folders : !rule.Files) continue;
            if (rule.Extensions is not null && !rule.Extensions.Contains(ext)) continue;
            if (rule.MaxBytes is long max && !isFolder && size > max) continue;
            var m = rule.Pattern.Match(name);
            if (!m.Success) continue;
            string? text = Render(rule.Text, m, rule.Lookups);
            if (text is null) continue;
            if (rule.Sibling is not null && rule.SiblingText is not null && siblingExists is not null &&
                Render(rule.Sibling, m, rule.Lookups) is { Length: > 0 } sibling && siblingExists(sibling))
                text += Render(rule.SiblingText, m, rule.Lookups);
            found.Add((rule, text));
        }
        var suppressed = found.SelectMany(f => f.Rule.Suppresses).ToHashSet();
        return found.Where(f => !suppressed.Contains(f.Rule.Id)).Select(f => new KnownDetail(f.Rule.Id, f.Text)).ToList();
    }

    /// <summary>For a folder: what the names of the files inside tell, once per convention ("Game files here are named the No-Intro way…").</summary>
    public IReadOnlyList<KnownDetail> ForFolderContents(IEnumerable<(string Name, long Size)> files)
    {
        var seen = new HashSet<string>();
        var list = new List<KnownDetail>();
        foreach (var (name, size) in files)
        {
            foreach (var d in ForName(name, isFolder: false, size))
            {
                var rule = _names.First(r => r.Id == d.Id);
                if (rule.FolderText is null || !seen.Add(rule.Id)) continue;
                list.Add(new KnownDetail(rule.Id, rule.FolderText));
            }
        }
        return list;
    }

    /// <summary>Fills in {group}, {date}, {languages}, {lookup:group} and {?group:text}; null when a date in the name isn't a real date.</summary>
    private static string? Render(string template, Match m, Dictionary<string, Dictionary<string, string>> lookups)
    {
        string text = ConditionalRx().Replace(template, c => m.Groups[c.Groups[1].Value].Success ? c.Groups[2].Value : "");
        if (text.Contains("{date}", StringComparison.Ordinal))
        {
            if (DateFrom(m) is not { } date) return null;
            text = text.Replace("{date}", date, StringComparison.Ordinal);
        }
        if (text.Contains("{languages}", StringComparison.Ordinal))
            text = text.Replace("{languages}", Languages(m.Groups["langs"].Value), StringComparison.Ordinal);
        text = LookupRx().Replace(text, l =>
        {
            string group = l.Groups[1].Value, value = m.Groups[group].Value;
            if (!lookups.TryGetValue(group, out var table)) return value;
            // The longest key the value starts with ("b1" → "b", "Rev 1" → "Rev").
            var key = table.Keys.Where(k => value.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
            return key is null ? value : table[key];
        });
        return GroupRx().Replace(text, g => m.Groups[g.Groups[1].Value] is { Success: true } grp ? grp.Value : "");
    }

    private static string? DateFrom(Match m)
    {
        if (!int.TryParse(m.Groups["y"].Value, out int y) || !int.TryParse(m.Groups["mo"].Value, out int mo) || !int.TryParse(m.Groups["d"].Value, out int d)) return null;
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        var date = new DateTime(y, mo, d);
        string text = date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        if (int.TryParse(m.Groups["h"].Value, out int h) && int.TryParse(m.Groups["mi"].Value, out int mi) && h < 24 && mi < 60)
            text += $" at {h:00}:{mi:00}";
        return text;
    }

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["En"] = "English", ["Fr"] = "French", ["De"] = "German", ["Es"] = "Spanish", ["It"] = "Italian", ["Nl"] = "Dutch", ["Pt"] = "Portuguese",
        ["Sv"] = "Swedish", ["No"] = "Norwegian", ["Da"] = "Danish", ["Fi"] = "Finnish", ["Ja"] = "Japanese", ["Zh"] = "Chinese", ["Ko"] = "Korean",
        ["Pl"] = "Polish", ["Ru"] = "Russian", ["Ca"] = "Catalan", ["Cs"] = "Czech", ["Hu"] = "Hungarian", ["El"] = "Greek", ["Tr"] = "Turkish", ["Ar"] = "Arabic",
    };

    private static string Languages(string codes)
    {
        var names = codes.Split(',', '+').Select(c => LanguageNames.TryGetValue(c.Trim(), out var n) ? n : c.Trim()).Where(n => n.Length > 0).ToList();
        return names.Count switch
        {
            0 => codes,
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    [GeneratedRegex(@"\{\?(\w+):((?:[^{}]|\{[^{}]*\})*)\}")]
    private static partial Regex ConditionalRx();

    [GeneratedRegex(@"\{lookup:(\w+)\}")]
    private static partial Regex LookupRx();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex GroupRx();

    // ---------------------------------------------------------------- loading

    public static KnownDetails Load(string file)
    {
        if (!File.Exists(file)) return Empty;
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(file), options);
        var root = doc.RootElement;

        static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static DateTime? D(JsonElement e, string name) =>
            S(e, name) is { } s ? DateTime.SpecifyKind(DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc) : null;

        var dates = new List<DateRule>();
        if (root.TryGetProperty("dates", out var ds))
        {
            foreach (var d in ds.EnumerateArray())
            {
                var local = D(d, "local");
                dates.Add(new DateRule(S(d, "id") ?? "", local is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Unspecified) : null,
                    D(d, "utc"), D(d, "utcFrom"), D(d, "utcTo"), S(d, "text") ?? "", S(d, "folderText")));
            }
        }

        var names = new List<NameRule>();
        if (root.TryGetProperty("names", out var ns))
        {
            foreach (var n in ns.EnumerateArray())
            {
                var lookups = new Dictionary<string, Dictionary<string, string>>();
                if (n.TryGetProperty("lookups", out var lk))
                    foreach (var group in lk.EnumerateObject())
                        lookups[group.Name] = group.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
                names.Add(new NameRule(
                    S(n, "id") ?? "",
                    n.TryGetProperty("files", out var f) && f.GetBoolean(),
                    n.TryGetProperty("folders", out var fo) && fo.GetBoolean(),
                    n.TryGetProperty("extensions", out var ex) ? ex.EnumerateArray().Select(x => (x.GetString() ?? "").ToLowerInvariant()).ToHashSet() : null,
                    n.TryGetProperty("maxBytes", out var mb) ? mb.GetInt64() : null,
                    new Regex(S(n, "pattern") ?? "$^", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
                    S(n, "text") ?? "",
                    S(n, "folderText"),
                    S(n, "sibling"),
                    S(n, "siblingText"),
                    lookups,
                    n.TryGetProperty("suppresses", out var sup) ? sup.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : []));
            }
        }
        var topics = new List<KnownTopic>();
        if (root.TryGetProperty("topics", out var ts))
        {
            static List<string> Strings(JsonElement e, string name) =>
                e.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
            foreach (var t in ts.EnumerateArray())
            {
                topics.Add(new KnownTopic(
                    S(t, "id") ?? "",
                    S(t, "name") ?? "",
                    new Regex(S(t, "phrases") ?? "$^", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
                    Strings(t, "details").ToHashSet(StringComparer.Ordinal),
                    Strings(t, "extensions").Select(x => x.ToLowerInvariant()).ToHashSet(),
                    Strings(t, "markers")));
            }
        }
        return new KnownDetails(dates, names, S(root, "futureDate") ?? "", topics);
    }
}
