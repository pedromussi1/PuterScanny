using System.Globalization;
using System.Text.Json;

namespace PuterScanny.Knowledge;

/// <summary>
/// The curated, offline knowledge shipped with the app: file types, content signatures and known locations.
/// Loaded from the <c>knowledge</c> folder next to the app.
/// </summary>
public sealed class KnowledgeBase
{
    private readonly Dictionary<string, FileTypeInfo> _types;
    private readonly Dictionary<string, FileCategory> _categories;
    private readonly Dictionary<string, LocationCategory> _locationCategories;
    private readonly Dictionary<string, LocationRule> _rulesById;

    private KnowledgeBase(
        IReadOnlyList<FileCategory> categories,
        IReadOnlyList<FileTypeInfo> types,
        IReadOnlyList<MagicSignature> signatures,
        IReadOnlyList<LocationCategory> locationCategories,
        IReadOnlyList<LocationRule> rules)
    {
        Categories = categories;
        Types = types;
        Signatures = signatures;
        LocationCategories = locationCategories;
        Rules = rules;
        _types = types.ToDictionary(t => t.Extension, StringComparer.OrdinalIgnoreCase);
        _categories = categories.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _locationCategories = locationCategories.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _rulesById = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        MaxSignatureLength = signatures.Count == 0 ? 0 : signatures.Max(s => s.RequiredLength);
    }

    public IReadOnlyList<FileCategory> Categories { get; }
    public IReadOnlyList<FileTypeInfo> Types { get; }
    public IReadOnlyList<MagicSignature> Signatures { get; }
    public IReadOnlyList<LocationCategory> LocationCategories { get; }
    public IReadOnlyList<LocationRule> Rules { get; }

    /// <summary>Small facts about how files get their names and dates (TorrentZIP's 1996 date, ROM and camera names…).</summary>
    public KnownDetails Details { get; private init; } = KnownDetails.Empty;

    /// <summary>Bytes from the start of a file needed to test every signature.</summary>
    public int MaxSignatureLength { get; }

    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "knowledge");

    public FileTypeInfo? GetType(string? extension) =>
        extension is not null && _types.TryGetValue(extension, out var t) ? t : null;

    public FileCategory? GetCategory(string? id) =>
        id is not null && _categories.TryGetValue(id, out var c) ? c : null;

    public string CategoryName(string? id) => GetCategory(id)?.Name ?? "Other";

    public LocationRule? GetRule(string? id) =>
        id is not null && _rulesById.TryGetValue(id, out var r) ? r : null;

    public string LocationCategoryName(string? id) =>
        id is not null && _locationCategories.TryGetValue(id, out var c) ? c.Name : "Other";

    /// <summary>Builds a matcher for all location rules, with paths expanded for this machine.</summary>
    public PathMatcher<LocationRule> BuildLocationMatcher(PathTokens tokens)
    {
        var matcher = new PathMatcher<LocationRule>();
        for (int i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i];
            foreach (string raw in rule.Paths)
            {
                string? expanded = tokens.Expand(raw);
                if (expanded is null) continue;
                matcher.Add(expanded, rule, Specificity(expanded) * 1000 + i);
            }
        }
        return matcher;
    }

    /// <summary>Literal folder names count most, wildcards less, '**' least; deeper patterns win ties.</summary>
    public static int Specificity(string pattern)
    {
        int score = 0;
        foreach (string seg in pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "**") score += 1;
            else if (seg.AsSpan().IndexOfAny('*', '?') >= 0) score += seg.Length > 2 ? 6 : 4;
            else score += 10;
        }
        return score;
    }

    // ---------------------------------------------------------------- loading

    public static KnowledgeBase LoadDefault() => Load(DefaultFolder);

    public static KnowledgeBase Load(string folder)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var typesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "file-types.json")), options);
        using var sigDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "signatures.json")), options);
        using var locDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "locations.json")), options);

        var categories = typesDoc.RootElement.GetProperty("categories").EnumerateArray()
            .Select(c => new FileCategory(Str(c, "id"), Str(c, "name"), Str(c, "description")))
            .ToList();

        var types = new List<FileTypeInfo>();
        foreach (var cat in typesDoc.RootElement.GetProperty("types").EnumerateObject())
        {
            foreach (var ext in cat.Value.EnumerateObject())
                types.Add(new FileTypeInfo(ext.Name.ToLowerInvariant(), ext.Value.GetString() ?? "", cat.Name));
        }

        var signatures = sigDoc.RootElement.GetProperty("signatures").EnumerateArray()
            .Select(s => new MagicSignature(
                Str(s, "id"),
                Str(s, "name"),
                Str(s, "category"),
                s.TryGetProperty("offset", out var off) ? off.GetInt32() : 0,
                ParseHex(Str(s, "hex")),
                s.TryGetProperty("extensions", out var exts) ? exts.EnumerateArray().Select(e => (e.GetString() ?? "").ToLowerInvariant()).ToList() : [],
                s.TryGetProperty("container", out var cont) && cont.GetBoolean(),
                s.TryGetProperty("weak", out var weak) && weak.GetBoolean()))
            .ToList();

        var locationCategories = locDoc.RootElement.GetProperty("categories").EnumerateArray()
            .Select(c => new LocationCategory(Str(c, "id"), Str(c, "name")))
            .ToList();

        var rules = locDoc.RootElement.GetProperty("rules").EnumerateArray()
            .Select(r => new LocationRule(
                Str(r, "id"),
                Str(r, "name"),
                Str(r, "description"),
                Str(r, "category"),
                r.TryGetProperty("owner", out var o) ? o.GetString() : null,
                r.TryGetProperty("resolver", out var res) ? res.GetString() : null,
                r.GetProperty("paths").EnumerateArray().Select(p => p.GetString() ?? "").ToList(),
                r.TryGetProperty("protect", out var prot) ? prot.GetString() : null,
                r.TryGetProperty("advice", out var adv) ? new LocationAdvice(
                    Str(adv, "verdict"),
                    Str(adv, "why"),
                    adv.TryGetProperty("minAgeDays", out var age) ? age.GetInt32() : 0,
                    adv.TryGetProperty("deleteFolder", out var df) && df.GetBoolean(),
                    adv.TryGetProperty("tool", out var tool) ? tool.GetString() : null) : null))
            .ToList();

        return new KnowledgeBase(categories, types, signatures, locationCategories, rules)
        {
            Details = KnownDetails.Load(Path.Combine(folder, "known-details.json")),
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    public static byte?[] ParseHex(string hex)
    {
        var parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new byte?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = parts[i] == "??" ? null : byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return result;
    }
}
