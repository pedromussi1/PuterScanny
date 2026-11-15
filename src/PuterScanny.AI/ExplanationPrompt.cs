using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PuterScanny.AI;

/// <summary>Everything PuterScanny knows about one item, as plain label/value facts. The AI sees only these.</summary>
public sealed record ExplainFacts(string Name, bool IsFolder, IReadOnlyList<KeyValuePair<string, string>> Facts)
{
    public static ExplainFacts Create(string name, bool isFolder, IEnumerable<(string Label, string? Value)> facts) =>
        new(name, isFolder, facts.Where(f => !string.IsNullOrWhiteSpace(f.Value))
                                 .Select(f => new KeyValuePair<string, string>(f.Label, f.Value!.Trim()))
                                 .ToList());
}

/// <summary>The instructions given to the local model, and the cache key for an answer.</summary>
public static class ExplanationPrompt
{
    /// <summary>Bumped whenever the instructions change, so older cached answers aren't reused.</summary>
    public const string Version = "1";

    public const string System = """
        You are the file explainer inside PuterScanny, a Windows disk-cleanup tool. A non-technical person found an item on their PC and wants to know what it is.

        Using the facts given and well-known general knowledge about Windows and common software, explain in plain English:
        what the item most likely is, which program, game, service or activity probably created it, and what it is used for.

        Rules:
        - Write 2 to 4 short sentences. No lists, headings, markdown or quotes.
        - Base everything on the facts. If they don't make it clear, say so honestly ("It is not clear what…", "It is probably…"). Never invent program names, companies or details that the facts don't support.
        - Do not say whether to keep, delete, remove or uninstall it, and do not say whether it is safe. PuterScanny decides that separately with its own checks.
        - Do not repeat the size, dates or path back; explain what they suggest instead.
        """;

    public static string BuildUserMessage(ExplainFacts facts)
    {
        var sb = new StringBuilder();
        sb.Append("What is this ").Append(facts.IsFolder ? "folder" : "file").Append("?\n\n");
        sb.Append("Name: ").Append(facts.Name).Append('\n');
        foreach (var (label, value) in facts.Facts)
        {
            string v = value.Replace("\r", "").Replace("\n", "; ");
            if (v.Length > 400) v = v[..400] + "…";
            sb.Append(label).Append(": ").Append(v).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Same facts + same model + same instructions = same answer, so it's computed only once.</summary>
    public static string CacheKey(string modelId, ExplainFacts facts)
    {
        string text = $"{Version}\n{modelId}\n{BuildUserMessage(facts)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Hides the Windows user name in paths (C:\Users\Name\… → C:\Users\&lt;you&gt;\…): it says nothing about the file.</summary>
    public static string MaskUserName(string path)
    {
        return Regex.Replace(path, @"^([A-Za-z]:\\Users\\)(?!Public\\|Default\\|All Users\\)[^\\]+", "$1<you>", RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// Cleans the model's answer and enforces the one hard rule: the AI explains, it never advises. Any sentence that
/// tells the user to keep, delete or uninstall something, or calls it safe, is removed, so an AI text can never
/// contradict PuterScanny's own verdict.
/// </summary>
public static partial class ExplanationCleaner
{
    public const int MaxSentences = 5;
    public const int MaxLength = 800;

    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw;

        // Reasoning some models write before answering.
        s = ThinkBlock().Replace(s, "");
        int openThink = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (openThink >= 0) s = s[..openThink]; // unfinished reasoning: drop it
        s = SpecialToken().Replace(s, "");

        // Markdown and list formatting.
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Heading().Replace(s, "");
        s = Bullet().Replace(s, "");
        s = Whitespace().Replace(s, " ").Trim();
        if (s.StartsWith("Answer:", StringComparison.OrdinalIgnoreCase)) s = s[7..].Trim();

        var kept = new List<string>();
        foreach (string sentence in SplitSentences(s))
        {
            if (IsAdvice(sentence)) continue;
            kept.Add(sentence);
            if (kept.Count == MaxSentences) break;
        }
        string result = string.Join(" ", kept).Trim();

        if (result.Length > MaxLength)
        {
            int cut = result.LastIndexOf(". ", MaxLength, StringComparison.Ordinal);
            result = cut > 100 ? result[..(cut + 1)] : result[..MaxLength].TrimEnd() + "…";
        }
        // Too little left to be useful (for example the model only gave advice).
        return result.Count(char.IsLetter) < 25 ? null : result;
    }

    public static bool IsAdvice(string sentence) => Advice().IsMatch(sentence);

    private static readonly string[] Abbreviations = ["e.g.", "i.e.", "etc.", "vs.", "approx.", "Inc.", "Ltd.", "Corp.", "Co.", "No."];

    private static IEnumerable<string> SplitSentences(string text)
    {
        // Abbreviations aren't sentence ends: hide their dots while splitting.
        foreach (string a in Abbreviations) text = text.Replace(a, a.Replace('.', '․'), StringComparison.OrdinalIgnoreCase);
        foreach (string part in SentenceBreak().Split(text))
        {
            string sentence = part.Replace('․', '.').Trim();
            if (sentence.Length > 0) yield return sentence;
        }
    }

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z""(])")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"<\|[^|>]{1,30}\|>|</?(s|start_of_turn|end_of_turn)>", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialToken();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s+.*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"(?m)^\s*(?:[-*•]|\d+[.)])\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("""
        \b(?:
            safe\s+to\s+(?:delete|remove|erase|clean|get\s+rid)
          | (?:is|it's|its|are)\s+(?:probably\s+|likely\s+|generally\s+|usually\s+)?(?:safe|fine|okay|ok)\s+to
          | (?:you|users?)\s+(?:can|could|should|may|might|must|need\s+to|will\s+want\s+to)\s+(?:safely\s+)?(?:delete|remove|erase|keep|uninstall|clean|get\s+rid|free|clear|leave|not\s+(?:delete|remove))
          | (?:i|we)\s+(?:would\s+)?(?:recommend|suggest|advise)
          | (?:recommend|recommended|advisable|wise|better)\s+to\s+(?:delete|remove|keep|uninstall|leave)
          | (?:do\s+not|don't|never|avoid)\s+(?:delete|deleting|remove|removing|touch|touching|uninstall)
          | (?:should|must|can|could|may|might)\s+(?:not\s+)?(?:safely\s+)?(?:be\s+)?(?:safely\s+)?(?:kept|deleted|removed|uninstalled|cleaned|cleared|erased|discarded)
          | (?:no\s+longer|not|isn't|is\s+not|aren't|are\s+not)\s+(?:really\s+)?(?:needed|necessary|required|useful)
          | \bunnecessary\b
          | \bharmless\s+to\s+(?:delete|remove)
          | (?:consider|try)\s+(?:deleting|removing|uninstalling|keeping|cleaning)
          | (?:deleting|removing)\s+(?:it|this|them|the\s+\w+)\s+(?:is|would|will|could|may|might|should)
        )
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Advice();
}
