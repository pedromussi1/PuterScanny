using System.Diagnostics;
using PuterScanny.AI;

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
    public async Task<ChatAnswer> AskAsync(string question, IProgress<ChatProgress>? progress, CancellationToken ct, LookupRequest? request = null)
    {
        var clock = Stopwatch.StartNew();
        question = question.Trim();
        string scope = _scope;

        if (request is null && SmallTalk(question) is { } reply)
            return new ChatAnswer { Text = reply, Source = AnswerSource.Help, Elapsed = clock.Elapsed };

        // 1. A question PuterScanny recognises: exact answer, no AI.
        var matched = request ?? await Task.Run(() => _matcher.Match(question, scope, _previous), ct);
        if (matched is not null)
        {
            var result = await Task.Run(() => lookup.Run(matched, scope), ct);
            Remember(question, matched, result);
            // "What is this?": with the local AI on, it explains what the item is from all the facts gathered (slower,
            // but it can say what the facts add up to). Its answer is checked like any other; if it fails, the facts are shown.
            if (matched.Explain && result.Description.StartsWith("details of ", StringComparison.Ordinal) && model is { IsAvailable: true })
            {
                progress?.Report(new ChatProgress($"Found it. Writing a description ({model.Name}, on this PC)…", result.Items));
                var described = await DescribeAsync(question, result, progress, ct);
                if (described is not null)
                {
                    return new ChatAnswer
                    {
                        Text = described,
                        Items = result.Items,
                        Source = AnswerSource.LocalAi,
                        Lookups = [result.Description],
                        ModelName = model.Name,
                        Elapsed = clock.Elapsed,
                    };
                }
                return new ChatAnswer
                {
                    Text = result.Answer,
                    Items = result.Items,
                    Source = AnswerSource.LocalAiFallback,
                    Lookups = [result.Description],
                    ModelName = model.Name,
                    Elapsed = clock.Elapsed,
                };
            }
            return new ChatAnswer
            {
                Text = result.Answer,
                Items = result.Items,
                Source = AnswerSource.Scan,
                Lookups = [result.Description],
                Elapsed = clock.Elapsed,
            };
        }

        // 2. Anything else: the local AI picks the lookups and words the answer.
        if (model is not { IsAvailable: true })
            return Help(question, clock.Elapsed);

        progress?.Report(new ChatProgress($"Working out what to look up ({model.Name}, on this PC)…"));
        string plan = await model.GenerateAsync(AiPlanner.System(lookup.Kb), AiPlanner.User(question, scope, _history), 400, AiPlanner.Grammar(), 0.1f, ct);
        var requests = AiPlanner.Parse(plan);
        if (requests.Count == 0) return Help(question, clock.Elapsed);

        var results = new List<LookupResult>();
        foreach (var r in requests) results.Add(await Task.Run(() => lookup.Run(r, scope), ct));
        // The specific list first; an overview's "biggest items" are background.
        var items = results.OrderBy(r => r.Request.Kind == LookupKind.Overview ? 1 : 0).SelectMany(r => r.Items).GroupBy(i => (i.EntryId, i.Path)).Select(g => g.First()).Take(ScanLookup.MaxCount).ToList();
        Remember(question, requests[^1], results[^1]);
        var lookups = results.Select(r => r.Description).ToList();
        progress?.Report(new ChatProgress($"Found it. Writing the answer ({model.Name})…", items));

        string user = AnswerChecker.User(question, results);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string raw = await model.GenerateAsync(AnswerChecker.System, user, 300, null, attempt == 0 ? 0.2f : 0f, ct);
            var outcome = AnswerChecker.Check(raw, question, results);
            if (outcome.Ok)
            {
                return new ChatAnswer
                {
                    Text = outcome.Text,
                    Items = items,
                    Source = AnswerSource.LocalAi,
                    Lookups = lookups,
                    ModelName = model.Name,
                    Elapsed = clock.Elapsed,
                };
            }
            if (attempt == 0)
            {
                progress?.Report(new ChatProgress($"Checking the answer against the scan… rewriting it ({model.Name})…", items));
                user += "\n\nUse only names, sizes and numbers that appear in the results above, written exactly the same way.";
            }
        }

        // The AI's wording didn't match the scan: show the exact answers instead.
        return new ChatAnswer
        {
            Text = string.Join(" ", results.Select(r => r.Answer)),
            Items = items,
            Source = AnswerSource.LocalAiFallback,
            Lookups = lookups,
            ModelName = model.Name,
            Elapsed = clock.Elapsed,
        };
    }

    /// <summary>The AI's description of one item, checked against the facts; null when it didn't pass twice.</summary>
    private async Task<string?> DescribeAsync(string question, LookupResult result, IProgress<ChatProgress>? progress, CancellationToken ct)
    {
        string user = AnswerChecker.User(question, [result]);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string raw = await model!.GenerateAsync(AnswerChecker.DescribeSystem, user, 450, null, attempt == 0 ? 0.2f : 0f, ct);
            var outcome = AnswerChecker.Check(raw, question, [result]);
            if (outcome.Ok) return outcome.Text;
            if (attempt == 0)
            {
                progress?.Report(new ChatProgress($"Checking the description against the scan… rewriting it ({model.Name})…", result.Items));
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
            return Help(question, TimeSpan.Zero).Text;
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

    private ChatAnswer Help(string question, TimeSpan elapsed)
    {
        string folder = ChatText.FolderName(_scope);
        string ai = model is null || !model.IsAvailable
            ? " To ask in your own words, turn on Local AI (button at the top right): it runs on this PC, free."
            : "";
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
}
