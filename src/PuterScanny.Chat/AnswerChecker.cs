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
        - Whether to keep or delete something is decided by the app's checks, not by you. Report the recommendation using its label from the results (for example "it's marked Review", "Recommended to delete", "No reason to remove"). Never give your own advice.
        - Don't refer to the app by name ("PuterScanny"); just say what is recommended.
        - Don't guess why files are there, what they were used for, or whether the user still needs them; say only what the results show.
        - If the results don't answer the question, say so and say what they do show. Never conclude that something doesn't exist because it isn't in these results: say what was looked up instead.
        - If part of the question can't be answered from the results (for example which photos are from a holiday, or what happened last summer), say plainly that the scan can't tell that, then say what the results do show. Never assume the items are what the question hoped for.
        - Refer to items by their names, never by position ("the first item", "the third one").
        - Give a reason for an item only when the results state it (its label and the reason in brackets). Don't sum items up with reasons of your own, like "old downloads".
        - Talk about the user's files themselves, not about "the results", "the lookup" or "the data".
        - Write 1 to 3 short sentences that answer the question directly. The exact results and items are shown under your answer, so don't list them all. Plain text, no lists, headings or markdown.
        """;

    /// <summary>For "what is this?": a short summary of what the item is and what it's for. The exact facts (sizes,
    /// contents, recommendation, dates) are shown under it, so the summary doesn't repeat them.</summary>
    public const string DescribeSystem = """
        You are PuterScanny's assistant. The user asked what a file or folder on their PC is. Write a short summary, using only the facts you are given:
        what it is, what it's part of (a program, a game, a mod, a dataset, a download, the user's own files...), and what it's for.
        The exact details (sizes, file counts, dates, the recommendation) are shown to the user right under your summary, so don't repeat numbers, dates or the recommendation.
        Rules:
        - Use names, sizes, counts and dates exactly as they are written in the facts. Don't add up, estimate or invent numbers or names.
        - You may state what the facts clearly add up to (for example: Bink game videos with scene names like "Joker_escapes" are the game's cutscenes; a readme saying to copy a folder into the game means it's an install copy). Say "probably" or "looks like" when you do.
        - Keep or delete is decided by the app's checks, not by you. Never give your own advice.
        - Don't refer to the app by name ("PuterScanny"), and don't explain how recommendations work in general: say what this item is and what is recommended for it.
        - For the recommendation, give the label and the reasons listed in the facts. Don't add reasons of your own (age, use, size) that the facts don't give as reasons, and don't mention percentages.
        - Talk to the user as "you", about the item itself, not about "the facts" or "the data" you were given.
        - Every sentence must add something new: no filler, no repeating, no summary at the end.
        - Write 2 or 3 short sentences of plain text. No lists, headings or markdown.
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
            string? advice = AdviceProblem(sentence, items, facts);
            if (advice is not null) { problems.Add(advice); continue; }
            if (RecommendationProblem(sentence, facts) is { } misreported) { problems.Add(misreported); continue; }
            // "The results only show that…": talk about the files, not the lookup. Such a sentence is dropped.
            if (MetaRx().IsMatch(sentence)) { problems.Add($"meta: {sentence}"); continue; }
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
        foreach (Match m in YearRx().Matches(text))
            if (!haystack.Contains(m.Value, StringComparison.Ordinal)) problems.Add($"year not in the results: {m.Value}");
        // "There are no ML models": only when a lookup really searched and came back empty.
        bool searchedAndEmpty = results.Any(r => r.Items.Count == 0 && r.Request.Kind is not (LookupKind.Overview or LookupKind.Types or LookupKind.Item));
        if (!searchedAndEmpty)
            foreach (string sentence in Sentences(text))
                if (AbsenceRx().IsMatch(sentence) &&
                    // "No reason found to remove it", "no record of it being opened" are facts about one item, not a search.
                    !Regex.IsMatch(sentence, @"\b(?:no|any|without) (?:reason|record|usage|recommendation|sign)s?\b|nothing found says", RegexOptions.IgnoreCase))
                    problems.Add($"says something isn't there, but no lookup searched for it: {sentence}");
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

        bool sizeOrNameProblem = problems.Any(p => !p.StartsWith("advice", StringComparison.Ordinal) && !p.StartsWith("meta", StringComparison.Ordinal));
        bool usable = !sizeOrNameProblem && text.Count(char.IsLetter) >= 20;
        return new Outcome(usable, text, problems);
    }

    // ---------------------------------------------------------------- advice

    /// <summary>A sentence telling the user to delete or keep something must agree with PuterScanny's recommendation for what it names.</summary>
    private static string? AdviceProblem(string sentence, IReadOnlyList<ChatItem> items, string facts)
    {
        bool delete = DeleteAdviceRx().IsMatch(sentence);
        bool keep = KeepAdviceRx().IsMatch(sentence);
        if (!delete && !keep) return null;
        // "X is recommended to delete, while Y is yours to keep": each part is judged with the items it names.
        string[] clauses = Regex.Split(sentence, @"[;,]\s+|\s+(?:while|whereas)\s+");
        if (clauses.Length > 1)
            return clauses.Any(c => AdviceProblem(c, items, facts) is not null) ? $"advice disagrees with the recommendations: {sentence}" : null;

        var named = items.Where(i => i.Name.Length >= 3 && sentence.Contains(i.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0)
        {
            // General statements are fine when they report PuterScanny's own labels ("PuterScanny recommends deleting…").
            // General statements are fine when they report the recommendations ("…can be deleted here", "marked Review").
            bool resultsSayDeletable = Regex.IsMatch(facts, @"can be deleted|Recommended to delete|Safe to delete", RegexOptions.IgnoreCase);
            // "It is safe to remove them because they're marked Review": "safe" needs a Safe/Recommended label, whatever else it says.
            if (Regex.IsMatch(sentence, @"\b(safe|ok|okay|fine) to (delete|remove|get rid of)\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(facts, @"Recommended to delete|Safe to delete", RegexOptions.IgnoreCase))
                return $"advice not backed by the recommendations: {sentence}";
            // "It is recommended to delete this item": only when that label is really there.
            if (Regex.IsMatch(sentence, @"\brecommend(?:ed|s)?(?: to)? (?:delet|remov)\w*", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(sentence, @"\b(not|isn't|aren't|never|no)\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(facts, @"Recommended to delete", RegexOptions.IgnoreCase))
                return $"advice not backed by the recommendations: {sentence}";
            return resultsSayDeletable && Regex.IsMatch(sentence, @"\bcan be (deleted|removed)\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(sentence, @"PuterScanny|recommended to delete|safe to delete|marked|labell?ed|recommendation", RegexOptions.IgnoreCase)
                ? null
                : $"advice not backed by PuterScanny: {sentence}";
        }
        if (delete && !keep && named.Any(i => !i.IsDeletable && i.Verdict is not null))
            return $"advice disagrees with PuterScanny: {sentence}";
        if (keep && !delete && named.Any(i => i.IsDeletable))
            return $"advice disagrees with PuterScanny: {sentence}";
        return null;
    }

    /// <summary>
    /// "PuterScanny recommends keeping it" when the recommendation in the facts is Review: a sentence reporting
    /// PuterScanny's recommendation must name the one the facts give.
    /// </summary>
    private static string? RecommendationProblem(string sentence, string facts)
    {
        var given = Regex.Matches(facts, @"^(?:PuterScanny's )?[Rr]ecommendation[^:\n]*: (?<v>[^(\n,]+)", RegexOptions.Multiline)
            .Select(m => Kind(m.Groups["v"].Value)).Where(k => k is not null).ToHashSet();
        if (given.Count == 0) return null;
        if (!Regex.IsMatch(sentence, @"\b(recommend\w*|marks?|marked|label\w*|rated)\b", RegexOptions.IgnoreCase))
            return null;
        // Only the claim itself, not the reasons after it ("…Review, because you might keep…").
        string claim = Regex.Split(sentence, @"\b(?:because|since|as it|noting|although|but|if|unless|so that|once|until)\b|[,;:—–]", RegexOptions.IgnoreCase)[0];
        // "PuterScanny never suggests deleting your files on its own" isn't a recommendation.
        if (Regex.IsMatch(claim, @"\b(never|not|no|nothing)\b|n't\b", RegexOptions.IgnoreCase)) return null;
        var claimed = new List<string>();
        if (Regex.IsMatch(claim, @"\b(keep|keeping|kept)\b", RegexOptions.IgnoreCase)) claimed.Add("keep");
        if (Regex.IsMatch(claim, @"\b(review|reviewing)\b", RegexOptions.IgnoreCase)) claimed.Add("review");
        if (Regex.IsMatch(claim, @"\b(delete|deleting|deletion|remove|removing|removal)\b", RegexOptions.IgnoreCase)) claimed.Add("delete");
        if (Regex.IsMatch(claim, @"\brequired\b", RegexOptions.IgnoreCase)) claimed.Add("required");
        return claimed.Any(c => !given.Contains(c)) ? $"recommendation misreported: {sentence}" : null;
    }

    private static string? Kind(string label)
    {
        string l = label.Trim().ToLowerInvariant();
        return l.StartsWith("required", StringComparison.Ordinal) ? "required"
            : l.StartsWith("keep", StringComparison.Ordinal) || l.StartsWith("no reason", StringComparison.Ordinal) ? "keep"
            : l.StartsWith("review", StringComparison.Ordinal) ? "review"
            : l.Contains("delete", StringComparison.Ordinal) ? "delete"
            : null;
    }

    // ---------------------------------------------------------------- parsing

    private static string Clean(string raw)
    {
        string s = Regex.Replace(raw, @"<think>.*?</think>", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<\|[^|]*\|>|</?think>", "");
        s = Regex.Replace(s, @"(?m)^\s{0,3}#{1,6}\s+", "");
        s = Regex.Replace(s, @"(?m)^\s*(?:[-*•]|\d+[.)])\s+", "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Regex.Replace(s, @"(?<![\w*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\w*])", "$1");
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

    [GeneratedRegex(@"\b(?:1[5-9]\d\d|20\d\d)\b")]
    private static partial Regex YearRx();

    [GeneratedRegex(@"\b(?:no|none of|not any|zero)\b[^.]*\b(?:found|were found|was found|in (?:your|the|this)|exist|on (?:your|this) pc)\b|\b(?:found|there (?:are|is)|you have|i see) (?:no|none|nothing|not any)\b|\bnothing (?:like that|matching)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AbsenceRx();

    [GeneratedRegex(@"[\w\-()\[\]]+(?:\.[\w\-()\[\]]+)*\.[A-Za-z][A-Za-z0-9]{1,4}\b")]
    private static partial Regex FileNameRx();

    [GeneratedRegex("[\"“](?<q>[^\"”]{3,80})[\"”]")]
    private static partial Regex QuotedRx();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z""“(])")]
    private static partial Regex SentenceRx();

    [GeneratedRegex(@"\b((you|i|we) (can|could|should|may|might want to|ought to)( safely| probably| definitely| only| just| also| then| still)? (delete|remove|get rid of|trash|clean up|uninstall|free up)|(safe|ok|okay|fine) to (delete|remove|get rid of)|can (safely )?be (deleted|removed)|recommend(ed|s)? (to )?(deleting|removing|delete|remove|that you delete)|no longer (needed|necessary)|consider (deleting|removing))\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteAdviceRx();

    [GeneratedRegex(@"\b((you|i|we) (should|must|need to|ought to) keep|(don'?t|do not|never|shouldn'?t|should not) (delete|remove|touch)|(must|should) (be kept|stay|not be (deleted|removed)))\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeepAdviceRx();

    /// <summary>Sentences about the lookup rather than the files: "the results show…", "according to the facts…".</summary>
    [GeneratedRegex(@"\b(?:the|these|those) (?:lookup )?(?:results?|lookups?|facts(?: given| provided)?|data(?: given| provided)?)\b(?! (?:folder|file))|\baccording to the (?:scan )?(?:results|facts|data)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRx();
}
