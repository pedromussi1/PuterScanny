using System.Diagnostics;
using PuterScanny.AI;
using PuterScanny.Storage;

namespace PuterScanny.Chat;

/// <summary>Progress while an answer is being made: a status line, and the items as soon as they're known.</summary>
public sealed record ChatProgress(string Status, IReadOnlyList<ChatItem>? Items = null);

/// <summary>
/// "Ask about this scan". Recognised questions are answered instantly from the scan. Other questions go to the local
/// AI (when it's set up): it picks read-only lookups, writes the answer from their results only, and the answer is
/// checked against those results before it's shown. Everything runs on this PC.
/// </summary>
public sealed class ChatEngine(ScanLookup lookup, IChatModel? model)
{
    private readonly QuestionMatcher _matcher = new(lookup);
    private readonly List<(string Question, string Looked)> _history = [];
    private LookupRequest? _previous;
    private string _scope = lookup.Db.GetRoot()?.Path ?? "";

    public ScanLookup Lookup => lookup;

    /// <summary>The folder questions are about (the scanned folder or drive by default).</summary>
    public string ScopePath
    {
        get => _scope;
        set
        {
            if (string.Equals(_scope, value, StringComparison.OrdinalIgnoreCase)) return;
            _scope = value;
            _previous = null;
        }
    }

    public bool AiAvailable => model?.IsAvailable == true;

    public void Reset()
    {
        _history.Clear();
        _previous = null;
    }

    /// <param name="request">A lookup chosen by the app itself (for "Ask about this folder"); null = work it out from the question.</param>
    /// <remarks>
    /// The local AI is used only when it helps, and only as much as this PC can handle (see <see cref="IChatModel.Capacity"/>):
    /// questions read with confidence are answered straight from the scan; when a reading is unsure, or a question isn't
    /// understood, the AI works out what it means and picks the lookups, but the answer still comes from the scan; on a
    /// fast PC it can add a short summary on top, after the facts are shown.
    /// </remarks>
    public async Task<ChatAnswer> AskAsync(string question, IProgress<ChatProgress>? progress, CancellationToken ct, LookupRequest? request = null)
    {
        var clock = Stopwatch.StartNew();
        question = question.Trim();
        string scope = _scope;

        if (request is null && SmallTalk(question) is { } reply)
            return new ChatAnswer { Text = reply, Source = AnswerSource.Help, Elapsed = clock.Elapsed };

        var capacity = Capacity();
        var matched = request ?? await Task.Run(() => _matcher.Match(question, scope, _previous), ct);
        if (matched is not null)
        {
            var result = await Task.Run(() => lookup.Run(matched, scope), ct);
            // Words of the question the reading doesn't account for ("Batman" in "the Batman mod files"): unsure.
            var unexplained = request is null ? _matcher.Unexplained(question, result) : [];
            if (unexplained.Count > 0 && capacity.CanInterpret)
            {
                var understood = await InterpretAsync(question, scope, result.Description, progress, ct, clock);
                if (understood is not null) return understood;
            }
            // Without the AI: words the reading couldn't place are usually names ("Pokemon", "Witcher"). Look for them in
            // the names of files and folders; when something has them, that's the answer.
            if (unexplained.Count > 0 && await Task.Run(() => NameSearch(matched, unexplained, scope), ct) is { } byName)
            {
                (matched, result, unexplained) = (byName.Request, byName, []);
            }
            // "anything from Viva Pinata?": a folder of that name came back empty, but other things carry the name.
            else if (result.Items.Count == 0 && request is null && matched.Folder is not null &&
                     !string.Equals(result.ScopePath, scope, StringComparison.OrdinalIgnoreCase) &&
                     await Task.Run(() => NameSearch(new LookupRequest { Kind = LookupKind.List }, [ChatText.FolderName(result.ScopePath)], scope), ct) is { } named)
            {
                result = named with { Answer = result.Answer + "\n\n" + named.Answer };
                matched = named.Request;
            }
            Remember(question, matched, result);
            bool describe = matched.Explain && result.Description.StartsWith("details of ", StringComparison.Ordinal);
            return new ChatAnswer
            {
                Text = unexplained.Count == 0
                    ? result.Answer
                    : result.Answer + $"\n\nI read this as: {result.Description}. {Words(unexplained)} didn't fit that reading, so this may not be what you meant.",
                Items = result.Items,
                Source = unexplained.Count == 0 ? AnswerSource.Scan : AnswerSource.ScanUnsure,
                Lookups = [result.Description],
                Note = unexplained.Count > 0 ? AiNotUsed(capacity) : null,
                ModelName = model?.Name,
                Elapsed = clock.Elapsed,
                Summary = describe && capacity.Summaries ? (p, c) => DescribeAsync(question, result, p, c) : null,
            };
        }

        // Not understood: the local AI works out what it means, when this PC can run it.
        if (capacity.CanInterpret && await InterpretAsync(question, scope, null, progress, ct, clock) is { } answer)
            return answer;
        return Help(question, clock.Elapsed, capacity);
    }

    /// <summary>
    /// A search of names for words: within the reading's own filters first ("Pokemon" among game files), then anywhere in
    /// the folder. Null when nothing carries them.
    /// </summary>
    private LookupResult? NameSearch(LookupRequest reading, IReadOnlyList<string> words, string scope)
    {
        var terms = words.Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Count(char.IsLetterOrDigit) >= 3 && !AiPlanner.IsTooGeneral(w)).Distinct().Take(6).ToList();
        if (terms.Count == 0) return null;
        var tries = new List<LookupRequest>();
        if (reading.Kind == LookupKind.List) tries.Add(reading with { Terms = terms, Name = null });
        tries.Add(new LookupRequest { Kind = LookupKind.List, Folder = reading.Folder, Terms = terms, Count = 10 });
        foreach (var t in tries)
        {
            var r = lookup.Run(t, scope);
            if (r.Items.Count > 0) return r;
        }
        return null;
    }

    /// <summary>What the local AI may do right now.</summary>
    private AiCapacity Capacity() =>
        model is null ? AiCapacity.Unavailable("the local AI isn't set up")
        : !model.IsAvailable ? AiCapacity.Unavailable("the local AI isn't set up, or is turned off")
        : model.Capacity;

    /// <summary>"Batman" / "Batman and Robin" / "Batman, Robin and Joker".</summary>
    private static string Words(IReadOnlyList<string> words) =>
        ChatText.Sentence(ChatText.Join(words.Select(w => $"\"{w}\"").ToList()));

    private static string? AiNotUsed(AiCapacity capacity) => capacity.Tier switch
    {
        AiTier.CantRun => $"The local AI could have checked what you meant, but it wasn't used: {capacity.Reason}.",
        AiTier.Off when capacity.Reason.Contains("isn't set up", StringComparison.Ordinal) =>
            "With Local AI set up (button at the top right), it could check what you meant.",
        _ => null,
    };

    /// <summary>
    /// The local AI works out what the question means and picks the lookups; the answer is the scan's own. On a fast PC
    /// it can add a short summary on top later. <paramref name="quickReading"/> is the app's own (unsure) reading, as a hint.
    /// </summary>
    private async Task<ChatAnswer?> InterpretAsync(string question, string scope, string? quickReading, IProgress<ChatProgress>? progress, CancellationToken ct, Stopwatch clock)
    {
        progress?.Report(new ChatProgress($"Working out what you mean ({model!.Name}, on this PC)…"));
        string plan = await model.GenerateAsync(AiPlanner.System(lookup.Kb), AiPlanner.User(question, scope, _history, quickReading), 400, AiPlanner.Grammar(lookup.Kb), 0.1f, ct);
        // An "item" that isn't the exact name of a file or folder ("Batman") is something asked about by name: search names
        // for it, with the words the model gave for it.
        var requests = AiPlanner.Parse(plan)
            .Select(r => r.Kind == LookupKind.Item && r.Name is { } n && !lookup.HasExactName(n, scope)
                ? new LookupRequest { Kind = LookupKind.List, Folder = r.Folder, Terms = r.Terms ?? [n.ToLowerInvariant()], Count = 10 }
                : r.Kind == LookupKind.List ? r : r with { Terms = null })
            .ToList();
        if (requests.Count == 0) return null;

        var results = new List<LookupResult>();
        foreach (var r in requests)
        {
            var found = await Task.Run(() => lookup.Run(r, scope), ct);
            bool narrowed = r.Filter != ListFilter.Any || r.Category is not null || r.TypeLabels is not null || r.ItemKind != EntryKind.Any;
            if (found.Items.Count == 0 && r.Kind == LookupKind.List && r.Terms is { Count: > 0 } && narrowed)
            {
                var wider = r with { Filter = ListFilter.Any, Category = null, TypeLabels = null, TypeName = null, ItemKind = EntryKind.Any };
                var again = await Task.Run(() => lookup.Run(wider, scope), ct);
                if (again.Items.Count > 0) found = again;
            }
            results.Add(found);
        }
        Remember(question, requests[^1], results[^1]);
        // The specific list first; an overview's "biggest items" are background.
        var items = results.OrderBy(r => r.Request.Kind == LookupKind.Overview ? 1 : 0).SelectMany(r => r.Items).GroupBy(i => (i.EntryId, i.Path)).Select(g => g.First()).Take(ScanLookup.MaxCount).ToList();
        bool allEmpty = results.All(r => r.Items.Count == 0 && r.Request.Kind is not (LookupKind.Overview or LookupKind.Types or LookupKind.Item));
        var capacity = Capacity();
        return new ChatAnswer
        {
            // The exact answers: what was looked up is always visible, so a wrong choice shows instead of misleading.
            Text = string.Join("\n\n", results.Select(r => r.Answer)),
            Items = items,
            Source = AnswerSource.AiUnderstood,
            Lookups = results.Select(r => r.Description).ToList(),
            ModelName = model.Name,
            Elapsed = clock.Elapsed,
            Summary = !allEmpty && capacity.Summaries ? (p, c) => SummarizeAsync(question, results, items, p, c) : null,
        };
    }

    /// <summary>A short direct answer on top of the exact ones, checked against them; null when it doesn't pass.</summary>
    private async Task<string?> SummarizeAsync(string question, IReadOnlyList<LookupResult> results, IReadOnlyList<ChatItem> items, IProgress<ChatProgress>? progress, CancellationToken ct)
    {
        string user = AnswerChecker.User(question, results);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string raw = await model!.GenerateAsync(AnswerChecker.System, user, 160, null, attempt == 0 ? 0.2f : 0f, ct);
            var outcome = AnswerChecker.Check(raw, question, results);
            if (outcome.Ok) return outcome.Text;
            if (attempt == 0)
            {
                progress?.Report(new ChatProgress($"Checking the summary against the scan… rewriting it ({model.Name})…", items));
                user += "\n\nUse only names, sizes and numbers that appear in the results above, written exactly the same way.";
            }
        }
        return null;
    }

    /// <summary>The AI's description of one item, checked against the facts; null when it didn't pass twice.</summary>
    private async Task<string?> DescribeAsync(string question, LookupResult result, IProgress<ChatProgress>? progress, CancellationToken ct)
    {
        string user = AnswerChecker.User(question, [result]);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string raw = await model!.GenerateAsync(AnswerChecker.DescribeSystem, user, 220, null, attempt == 0 ? 0.2f : 0f, ct);
            var outcome = AnswerChecker.Check(raw, question, [result]);
            if (outcome.Ok) return outcome.Text;
            if (attempt == 0)
            {
                progress?.Report(new ChatProgress($"Checking the summary against the scan… rewriting it ({model.Name})…", result.Items));
                user += "\n\nUse only names, sizes and numbers that appear in the facts above, written exactly the same way.";
            }
        }
        return null;
    }

    private string? SmallTalk(string question)
    {
        string q = question.Trim().TrimEnd('!', '.', '?', ' ').ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(q, @"^(hi|hello|hey|hiya|good (morning|afternoon|evening)|yo)( there)?$"))
            return $"Hi! Ask me anything about this scan of {ChatText.FolderName(_scope)}: what's taking space, what can go, what something is, or why something is recommended.";
        if (System.Text.RegularExpressions.Regex.IsMatch(q, @"^(thanks|thank you|thx|ty|cheers|great|perfect|cool|nice|ok|okay|got it)( (so much|a lot))?$"))
            return "You're welcome. Ask again any time.";
        if (System.Text.RegularExpressions.Regex.IsMatch(q, @"^(help|what can (you|i) (do|ask)|how does this work)$"))
            return Help(question, TimeSpan.Zero, Capacity()).Text;
        return null;
    }

    private void Remember(string question, LookupRequest request, LookupResult result)
    {
        // After describing a folder, "and which of those…" means the things inside that folder.
        string folder = request.Kind == LookupKind.Item && result.Description.StartsWith("details of ", StringComparison.Ordinal) &&
                        result.Items is [{ IsFolder: true } described, ..]
            ? described.Path
            : result.ScopePath;
        _previous = request with { Folder = folder };
        _history.Add((question, result.Description));
        if (_history.Count > 6) _history.RemoveAt(0);
    }

    private ChatAnswer Help(string question, TimeSpan elapsed, AiCapacity capacity)
    {
        string folder = ChatText.FolderName(_scope);
        string ai = capacity.Tier switch
        {
            AiTier.CantRun => $" The local AI could have worked out what you meant, but it wasn't used: {capacity.Reason}.",
            AiTier.Off when capacity.Reason.Contains("turned off", StringComparison.Ordinal) => " To ask in your own words, turn on Local AI (button at the top right).",
            AiTier.Off => " To ask in your own words, set up Local AI (button at the top right): it runs on this PC, free.",
            _ => "",
        };
        return new ChatAnswer
        {
            Text = $"I didn't understand that question. I can answer things like \"What can I delete in {folder}?\", \"What's taking the most space?\", " +
                   "\"Which videos haven't I watched in a year?\", \"Show me duplicates\", \"Which programs don't I use?\" or \"What is <a file or folder name>?\"." + ai,
            Source = AnswerSource.Help,
            Elapsed = elapsed,
        };
    }

    /// <summary>Questions to suggest for the current folder.</summary>
    public IReadOnlyList<string> Suggestions()
    {
        string folder = ChatText.FolderName(_scope);
        return
        [
            $"What's taking the most space in {folder}?",
            $"What can I safely delete in {folder}?",
            "Which files haven't I opened in a year?",
            "Are there any duplicates?",
            "What should I review myself?",
            "Which programs don't I use?",
        ];
    }
}

/// <summary>The app's local AI (see <see cref="ExplanationService"/>) as a chat model.</summary>
public sealed class LocalAiChatModel(ExplanationService service) : IChatModel
{
    public string Name => service.ResolveModel()?.Name ?? "the local AI";
    public bool IsAvailable => service.IsAvailable;

    public Task<string> GenerateAsync(string system, string user, int maxTokens, string? grammar, float temperature, CancellationToken ct) =>
        service.GenerateAsync(system, user, maxTokens, grammar, temperature, ct);

    public AiCapacity Capacity => service.Capacity();
}
