using System.Text.RegularExpressions;

namespace PuterScanny.Chat;

/// <summary>
/// How sure the matcher is: after it reads a question, every meaningful word should be accounted for, either by
/// what it knows how to ask (deleting, unused, biggest, kinds of file, topics…) or by the answer itself (the folder,
/// the item's name, the items listed). Words left over ("Batman" in "is it safe to remove the Batman mod files?") mean
/// the reading may have missed the point.
/// </summary>
public sealed partial class QuestionMatcher
{
    /// <summary>Words that carry no meaning of their own in a question about files.</summary>
    private static readonly HashSet<string> Ordinary = new(StringComparer.Ordinal)
    {
        "the", "and", "but", "for", "nor", "yet", "with", "without", "from", "into", "onto", "about", "over", "under", "after", "before",
        "between", "since", "than", "then", "that", "this", "these", "those", "there", "here", "what", "which", "who", "whom", "whose",
        "where", "when", "why", "how", "are", "was", "were", "been", "being", "does", "did", "done", "doing", "have", "has", "had", "having",
        "can", "could", "should", "would", "will", "shall", "may", "might", "must", "not", "yes", "any", "anything", "some", "something",
        "all", "every", "each", "much", "many", "more", "most", "less", "least", "few", "lot", "lots", "very", "really", "just", "only",
        "also", "too", "still", "even", "ever", "again", "else", "other", "others", "please", "thanks", "okay", "like", "look", "looks",
        "looking", "seem", "seems", "think", "know", "want", "need", "needs", "needed", "get", "got", "give", "show", "tell", "list", "find",
        "see", "check", "let", "make", "made", "take", "takes", "taking", "took", "put", "keep", "kept", "keeping", "delete", "deleting",
        "deleted", "remove", "removing", "removed", "clean", "cleaning", "free", "safe", "safely", "worth", "use", "used", "using", "uses",
        "open", "opened", "file", "files", "folder", "folders", "item", "items", "thing", "things", "stuff", "space", "size", "sizes",
        "big", "bigger", "biggest", "large", "larger", "largest", "small", "smaller", "smallest", "old", "older", "oldest", "new", "newer",
        "newest", "recent", "recently", "latest", "last", "first", "top", "computer", "drive", "disk", "everything", "nothing", "one",
        "ones", "lying", "around", "left", "leave", "leaving", "anymore", "currently", "actually", "maybe", "probably", "inside", "within",
        "whole", "total", "year", "years", "month", "months", "week", "weeks", "day", "days", "time", "times", "ago", "you", "your",
        "yours", "our", "ours", "mine", "its", "it's", "i'm", "i've", "i'd", "i'll", "don't", "doesn't", "didn't", "haven't", "hasn't",
        "can't", "won't", "isn't", "aren't", "wasn't", "weren't", "shouldn't", "wouldn't", "couldn't", "what's", "where's", "that's",
        "there's", "here's", "who's", "let's", "currently", "able", "still", "kind", "kinds", "type", "types", "sort", "lot", "bit",
        "well", "good", "bad", "fine", "sure", "help", "any", "into", "per", "via", "each", "both", "either", "neither", "own", "same",
        "such", "way", "ways", "part", "parts", "place", "places", "stored", "store", "saved", "save", "kept", "sitting", "taking", "up",
        "anywhere", "somewhere", "everywhere", "pc", "laptop", "machine", "system", "windows", "app", "copy", "copies", "these",
    };

    /// <summary>What the matcher knows how to ask about, beyond categories, platforms and topics.</summary>
    private static readonly Regex[] Vocabulary =
    [
        new(@"\b(duplicates?|duplicated|identical|twice|double)\b", Opts),
        new(@"\b(mods?|modded|modding|nexus)\b", Opts),
        new(@"\b(leftovers?|left ?over|remnants?|traces?|uninstalled|removed programs?)\b", Opts),
        new(@"\b(empty|broken|dead|shortcuts?|links?|unfinished|partial|incomplete|failed|interrupted|downloads?|downloaded|downloading)\b", Opts),
        new(@"\b(programs?|apps?|applications?|software|games?|installed|install|uninstall|uninstalling)\b", Opts),
        new(@"\b(important|required|essential|critical|reason|reasons|unknown|unidentified|identify|identified|mystery|mysterious|unrecogni[sz]ed|unexplained)\b", Opts),
        new(@"\b(deletable|removable|cleanup|reclaim|junk|clutter|trash|waste|wasted|wasting|review|reviewing|unsure|decide|myself)\b", Opts),
        new(@"\b(unused|untouched|forgotten|stale|abandoned|watch|watched|play|played|listen|listened|launch|launched|run|ran|touch|touched)\b", Opts),
        new(@"\b(breakdown|summary|summari[sz]e|overview|status|describe|explain|details?|info|information|contents?|contain|contains|holds?)\b", Opts),
        new(@"\b(huge|heaviest|bulkiest|hogs?|hogging|eating|full|gb|mb|kb|tb|bytes?)\b", Opts),
        new(@"\b(least|recently|newest|oldest|latest|modified|changed|updated|added|grabbed)\b", Opts),
        new(@"\b(today|yesterday|week|month|year|decade|ages|forever|long)\b", Opts),
        new(@"\b(why|marked|flagged|labell?ed|recommended|recommendation|considered)\b", Opts),
    ];

    /// <summary>Meaningful words of the question that neither the reading nor its answer accounts for (empty = sure).</summary>
    public IReadOnlyList<string> Unexplained(string question, LookupResult result)
    {
        string low = question.ToLowerInvariant();
        var covered = new bool[low.Length];
        void Cover(Regex rx)
        {
            foreach (Match m in rx.Matches(low))
                for (int i = m.Index; i < m.Index + m.Length; i++) covered[i] = true;
        }
        foreach (var rx in Vocabulary) Cover(rx);
        Cover(UnusedRx());
        foreach (var (pattern, _) in CategoryWords) Cover(new Regex($@"\b(?:{pattern})\b", Opts));
        foreach (var (words, _, _) in Platforms) Cover(new Regex(words, Opts));
        foreach (var topic in lookup.Kb.Details.Topics) Cover(topic.Phrases);

        // What the answer is about: the lookup, its folder, and the names of what it found.
        string about = string.Join(" ", new[] { result.Description, result.ScopePath }
            .Concat(result.Items.Take(ScanLookup.MaxCount).SelectMany(i => new[] { i.Name, i.Path, i.Note, i.Headline }))).ToLowerInvariant();

        var left = new List<string>();
        foreach (Match m in WordRx().Matches(low))
        {
            string w = m.Value.Trim('\'');
            if (w.Length < 3 || w.All(char.IsDigit) || Ordinary.Contains(w)) continue;
            bool done = false;
            for (int i = m.Index; i < m.Index + m.Length && !done; i++) done = covered[i];
            if (done) continue;
            string stem = w.EndsWith("ies", StringComparison.Ordinal) ? w[..^3] + "y" : w.EndsWith('s') ? w[..^1] : w;
            if (about.Contains(w, StringComparison.Ordinal) || stem.Length >= 3 && about.Contains(stem, StringComparison.Ordinal)) continue;
            if (!left.Contains(w)) left.Add(w);
        }
        return left;
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'_\-]*")]
    private static partial Regex WordRx();
}
