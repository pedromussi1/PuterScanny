using System.Globalization;
using System.Text.RegularExpressions;

namespace PuterScanny.Chat;

/// <summary>
/// Checks an AI-written answer against the lookup results it was given. Every size, count, percentage and file name
/// in it must appear in those results, and keep/delete advice is only allowed where it matches PuterScanny's own
/// recommendation. Answers that fail are not shown.
/// </summary>
public static partial class AnswerChecker
{
    public const string System = """
        You are PuterScanny's assistant. You answer the user's question about their PC using only the lookup results you are given.
        Rules:
        - Use names, sizes, counts and dates exactly as they are written in the results. Don't add up, estimate or invent numbers.
        - Whether to keep or delete something is decided by PuterScanny, not by you. Say what PuterScanny recommends, using its label from the results (for example "Recommended to delete", "Review", "Keep", "No reason to remove"). Never give your own advice.
        - Don't guess why files are there, what they were used for, or whether the user still needs them; say only what the results show.
        - If the results don't answer the question, say so and say what they do show.
        - Write 2 to 5 short sentences of plain text. No lists, headings or markdown: the items are shown under your answer.
        """;

    public static string User(string question, IEnumerable<LookupResult> results) =>
        $"Question: {question}\n\nLookup results from the scan:\n{string.Join("\n", results.Select(r => r.FactsText.Trim()))}";

    public sealed record Outcome(bool Ok, string Text, IReadOnlyList<string> Problems);

    public static Outcome Check(string raw, string question, IReadOnlyList<LookupResult> results)
    {
        string facts = string.Join("\n", results.Select(r => r.FactsText + "\n" + r.Answer));
        var items = results.SelectMany(r => r.Items).ToList();
        var problems = new List<string>();

        string text = Clean(raw);
        var kept = new List<string>();
        foreach (string sentence in Sentences(text))
        {
            string? advice = AdviceProblem(sentence, items);
            if (advice is not null) { problems.Add(advice); continue; }
            kept.Add(sentence);
        }
        text = string.Join(" ", kept).Trim();

        var allowedSizes = Sizes(facts + "\n" + question).ToList();
        foreach (var (value, shown, approx) in SizesWithContext(text))
        {
            double tolerance = approx ? 0.06 : 0.015;
            if (!allowedSizes.Any(a => Math.Abs(a - value) <= Math.Max(1, a * tolerance)))
                problems.Add($"size not in the results: {shown}");
        }

        var allowedNumbers = Numbers(facts + "\n" + question);
        foreach (Match m in CountRx().Matches(text))
        {
            string digits = Digits(m.Groups["n"].Value);
            if (!allowedNumbers.Contains(digits)) problems.Add($"number not in the results: {m.Value}");
        }

        string haystack = (facts + "\n" + question).ToLowerInvariant();
        foreach (Match m in FileNameRx().Matches(text))
        {
            if (m.Value.Length < 5 || Regex.IsMatch(m.Value, @"^(e\.g|i\.e|etc|vs)\b", RegexOptions.IgnoreCase)) continue;
            if (!haystack.Contains(m.Value.ToLowerInvariant())) problems.Add($"name not in the results: {m.Value}");
        }
        foreach (Match m in QuotedRx().Matches(text))
        {
            string q = m.Groups["q"].Value.Trim();
            if (q.Length >= 3 && !haystack.Contains(q.ToLowerInvariant())) problems.Add($"quote not in the results: \"{q}\"");
        }

        bool sizeOrNameProblem = problems.Any(p => !p.StartsWith("advice", StringComparison.Ordinal));
        bool usable = !sizeOrNameProblem && text.Count(char.IsLetter) >= 20;
        return new Outcome(usable, text, problems);
    }

    // ---------------------------------------------------------------- advice

    /// <summary>A sentence telling the user to delete or keep something must agree with PuterScanny's recommendation for what it names.</summary>
    private static string? AdviceProblem(string sentence, IReadOnlyList<ChatItem> items)
    {
        bool delete = DeleteAdviceRx().IsMatch(sentence);
        bool keep = KeepAdviceRx().IsMatch(sentence);
        if (!delete && !keep) return null;

        var named = items.Where(i => i.Name.Length >= 3 && sentence.Contains(i.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0)
        {
            // General statements are fine when they report PuterScanny's own labels ("PuterScanny recommends deleting…").
            return Regex.IsMatch(sentence, @"PuterScanny|recommended to delete|safe to delete|marked|labell?ed|recommendation", RegexOptions.IgnoreCase)
                ? null
                : $"advice not backed by PuterScanny: {sentence}";
        }
        if (delete && !keep && named.Any(i => !i.IsDeletable && i.Verdict is not null))
            return $"advice disagrees with PuterScanny: {sentence}";
        if (keep && !delete && named.Any(i => i.IsDeletable))
            return $"advice disagrees with PuterScanny: {sentence}";
        return null;
    }

    // ---------------------------------------------------------------- parsing

    private static string Clean(string raw)
    {
        string s = Regex.Replace(raw, @"<think>.*?</think>", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<\|[^|]*\|>|</?think>", "");
        s = Regex.Replace(s, @"(?m)^\s{0,3}#{1,6}\s+", "");
        s = Regex.Replace(s, @"(?m)^\s*(?:[-*•]|\d+[.)])\s+", "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static IEnumerable<string> Sentences(string text) =>
        SentenceRx().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0);

    private static double ToBytes(string number, string unit)
    {
        string n = number.Replace(" ", "").Replace(" ", "");
        // "1,234.5" or "1.234,5" or "6,64": the last separator followed by 1-2 digits is the decimal point.
        var dec = Regex.Match(n, @"[.,](\d{1,2})$");
        string normalized = dec.Success
            ? Regex.Replace(n[..dec.Index], @"[.,]", "") + "." + dec.Groups[1].Value
            : Regex.Replace(n, @"[.,]", "");
        double v = double.Parse(normalized, CultureInfo.InvariantCulture);
        return unit.ToUpperInvariant() switch
        {
            "KB" => v * 1024,
            "MB" => v * 1024 * 1024,
            "GB" => v * 1024 * 1024 * 1024,
            "TB" => v * 1024 * 1024 * 1024 * 1024,
            _ => v,
        };
    }

    private static IEnumerable<double> Sizes(string text) =>
        SizeRx().Matches(text).Select(m => ToBytes(m.Groups["v"].Value, m.Groups["u"].Value));

    private static IEnumerable<(double Value, string Shown, bool Approx)> SizesWithContext(string text)
    {
        foreach (Match m in SizeRx().Matches(text))
        {
            string before = text[Math.Max(0, m.Index - 16)..m.Index].ToLowerInvariant();
            bool approx = Regex.IsMatch(before, @"(about|around|roughly|nearly|almost|over|more than|under|less than|approximately|close to|~)\s*$");
            yield return (ToBytes(m.Groups["v"].Value, m.Groups["u"].Value), m.Value, approx);
        }
    }

    private static HashSet<string> Numbers(string text) =>
        NumberRx().Matches(text).Select(m => Digits(m.Value)).ToHashSet();

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    [GeneratedRegex(@"(?<v>\d{1,3}(?:[,.  ]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?)\s?(?<u>bytes|KB|MB|GB|TB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRx();

    [GeneratedRegex(@"\b(?<n>\d[\d,. ]*)\s+(?:files?|folders?|items?|copies|copy|programs?|sets?|times?|apps?)\b|\b(?<n>\d+)\s?%", RegexOptions.IgnoreCase)]
    private static partial Regex CountRx();

    [GeneratedRegex(@"\d[\d,. ]*")]
    private static partial Regex NumberRx();

    [GeneratedRegex(@"[\w\-()\[\]]+(?:\.[\w\-()\[\]]+)*\.[A-Za-z][A-Za-z0-9]{1,4}\b")]
    private static partial Regex FileNameRx();

    [GeneratedRegex("[\"“](?<q>[^\"”]{3,80})[\"”]")]
    private static partial Regex QuotedRx();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z""“(])")]
    private static partial Regex SentenceRx();

    [GeneratedRegex(@"\b((you|i|we) (can|could|should|may|might want to|ought to)( safely| probably| definitely)? (delete|remove|get rid of|trash|clean up|uninstall|free up)|(safe|ok|okay|fine) to (delete|remove|get rid of)|can (safely )?be (deleted|removed)|recommend(ed|s)? (deleting|removing|that you delete)|no longer (needed|necessary)|consider (deleting|removing))\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteAdviceRx();

    [GeneratedRegex(@"\b((you|i|we) (should|must|need to|ought to) keep|(don'?t|do not|never|shouldn'?t|should not) (delete|remove|touch)|(must|should) (be kept|stay|not be (deleted|removed)))\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeepAdviceRx();
}
