using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuterScanny.Chat;
using PuterScanny.Core.Models;
using PuterScanny.Storage;

namespace PuterScanny.App.ViewModels;

/// <summary>A file, folder or program under an answer, with ways to look at it.</summary>
public sealed partial class ChatItemRow(ChatItem item, ChatViewModel owner, CleanupRow? basketRow) : ObservableObject
{
    public ChatItem Item { get; } = item;
    public string Name => Item.Name;
    public string SizeText => Item.SizeText;
    public string Note => Item.Note;
    public string Path => Item.Path;
    public string VerdictText => Item.VerdictText;
    public bool HasVerdict => Item.VerdictText.Length > 0;
    public string Headline => Item.Headline;

    /// <summary>Colour key for the verdict pill ("Delete", "Review"…; anything else is shown neutral).</summary>
    public string VerdictTag => Display.VerdictTag(Item.Verdict, Item.Action) is { Length: > 0 } tag ? tag : "Keep";

    public bool IsKeep => VerdictTag == "Keep";
    public string Glyph => Item.IsFolder ? "" : "";

    /// <summary>The row in the Clean up tab, when this item can go in the clean-up basket.</summary>
    public CleanupRow? BasketRow { get; } = basketRow;

    public bool CanBasket => BasketRow is { CanSelect: true };
    public bool CanShow => Item.EntryId is not null;
    public bool CanReveal => Item.Path.Length > 0;

    [RelayCommand]
    private void Show() => owner.ShowInFolders?.Invoke(Item);

    [RelayCommand]
    private void Reveal() => owner.Reveal?.Invoke(Item.Path);

    [RelayCommand]
    private Task AskAbout() => Item.EntryId is not null
        ? owner.AskWithLookupAsync($"What is {Item.Name}?", new LookupRequest { Kind = LookupKind.Item, Name = Item.Path, Explain = true })
        : owner.AskCommand.ExecuteAsync($"What is {Item.Name}?");
}

/// <summary>One message in the chat: the user's question, or an answer with the items it's based on.</summary>
public sealed partial class ChatMessage : ObservableObject
{
    public bool IsUser { get; init; }
    public bool IsAnswer => !IsUser;

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial string Credit { get; set; } = "";

    public ObservableCollection<ChatItemRow> Items { get; } = [];
}

/// <summary>
/// The "Ask" tab: questions about the open scan, answered from the scan itself (and, for questions in other words,
/// by the optional local AI working from exact lookups). Everything stays on this PC.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly AiController _ai;
    private ScanDatabase? _db;
    private ChatEngine? _engine;
    private string _rootPath = "";
    private Func<long, CleanupRow?> _findBasketRow = _ => null;
    private CancellationTokenSource? _cts;

    public ChatViewModel(AiController ai)
    {
        _ai = ai;
        _ai.Service.StatusChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateAiNote);
        UpdateAiNote();
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];

    /// <summary>Set by the main view model.</summary>
    public Action<ChatItem>? ShowInFolders { get; set; }
    public Action<string>? Reveal { get; set; }

    /// <summary>Raised when a question is asked, so the view can bring it to the top with its answer below.</summary>
    public event EventHandler<ChatMessage>? QuestionAsked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial string Input { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand), nameof(StopCommand), nameof(AskAboutWholeScanCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string ScopeText { get; set; } = "";
    [ObservableProperty] public partial bool IsScoped { get; set; }
    [ObservableProperty] public partial string AiNote { get; set; } = "";
    [ObservableProperty] public partial bool ShowAiSetup { get; set; }

    public bool HasMessages => Messages.Count > 0;

    // ---------------------------------------------------------------- scan open / closed

    public void Open(ResultsContext ctx, Func<long, CleanupRow?> findBasketRow)
    {
        Close();
        // Its own connection, so answers can be looked up in the background while the rest of the app reads the scan.
        _db = ScanDatabase.Open(ctx.Db.FilePath);
        _engine = new ChatEngine(new ScanLookup(_db, ctx.Kb), new LocalAiChatModel(_ai.Service));
        _rootPath = _engine.ScopePath;
        _findBasketRow = findBasketRow;
        SetScope(_rootPath);
    }

    public void Close()
    {
        _cts?.Cancel();
        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
        Suggestions.Clear();
        _engine = null;
        // An answer may still be reading the scan in the background: close it once that's done.
        var db = _db;
        _db = null;
        if (db is not null)
        {
            if (_running is { IsCompleted: false } running) running.ContinueWith(_ => db.Dispose(), TaskScheduler.Default);
            else db.Dispose();
        }
        ScopeText = "";
        IsScoped = false;
    }

    /// <summary>Points the questions at a folder (from "Ask about this folder").</summary>
    public void SetScope(string folderPath)
    {
        if (_engine is null) return;
        _engine.ScopePath = folderPath;
        IsScoped = !string.Equals(folderPath, _rootPath, StringComparison.OrdinalIgnoreCase);
        ScopeText = folderPath;
        Suggestions.Clear();
        foreach (string s in _engine.Suggestions()) Suggestions.Add(s);
        UpdateAiNote();
        AskAboutWholeScanCommand.NotifyCanExecuteChanged();
    }

    private bool CanAskAboutWholeScan() => IsScoped && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAskAboutWholeScan))]
    private void AskAboutWholeScan() => SetScope(_rootPath);

    private void UpdateAiNote()
    {
        bool ai = _ai.Service.IsAvailable;
        ShowAiSetup = !ai;
        AiNote = ai
            ? $"Answers come straight from the scan. When a question isn't clear, {_ai.ModelName} on this PC works out what you mean (as much as this PC can comfortably run), and anything it writes is checked against the scan."
            : "Answers come straight from this scan, instantly. Set up Local AI to also ask in your own words: it's free, runs on this PC, and is only used when it helps.";
    }

    // ---------------------------------------------------------------- asking

    private bool CanAsk(string? text) => !IsBusy && _engine is not null && ((text ?? Input).Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private Task AskAsync(string? text)
    {
        string question = (text ?? Input).Trim();
        if (text is null) Input = "";
        return RunAsync(question, null);
    }

    /// <summary>Asks with a lookup the app picked (shown as <paramref name="question"/>).</summary>
    public Task AskWithLookupAsync(string question, LookupRequest request) => IsBusy ? Task.CompletedTask : RunAsync(question, request);

    private Task? _running;

    private Task RunAsync(string question, LookupRequest? request)
    {
        if (_engine is null || question.Length == 0) return Task.CompletedTask;
        _running = RunCoreAsync(_engine, question, request);
        return _running;
    }

    private async Task RunCoreAsync(ChatEngine engine, string question, LookupRequest? request)
    {

        var asked = new ChatMessage { IsUser = true, Text = question };
        Messages.Add(asked);
        var answer = new ChatMessage { IsWorking = true, Status = "Looking it up in the scan…" };
        Messages.Add(answer);
        OnPropertyChanged(nameof(HasMessages));
        QuestionAsked?.Invoke(this, asked);

        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ChatProgress>(p =>
            {
                answer.Status = p.Status;
                if (p.Items is { } items && answer.Items.Count == 0) AddItems(answer, items);
            });
            var result = await engine.AskAsync(question, progress, _cts.Token, request);
            answer.Text = result.Text;
            answer.Items.Clear();
            AddItems(answer, result.Items);
            answer.Credit = Credit(result);

            // The facts are shown; on a fast PC the local AI can add a short summary on top. Stop leaves the facts as they are.
            if (result.Summary is { } summarize)
            {
                answer.IsWorking = true;
                answer.Status = $"Adding a short summary ({result.ModelName}, on this PC)…";
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    string? summary = await summarize(progress, _cts.Token);
                    answer.Credit = Credit(result) + (summary is not null
                        ? $" The summary on top was written by {result.ModelName} on this PC ({clock.Elapsed.TotalSeconds:0} s) and checked against the scan."
                        : $" {result.ModelName}'s summary didn't match the scan exactly, so it isn't shown.");
                    if (summary is not null) answer.Text = summary + "\n\n" + answer.Text;
                }
                catch (OperationCanceledException)
                {
                    answer.Credit = Credit(result) + " The summary was stopped.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (answer.Text.Length == 0) answer.Text = "Stopped.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            answer.Text = $"Something went wrong while answering: {ex.Message}";
        }
        finally
        {
            answer.IsWorking = false;
            answer.Status = "";
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            _ai.Refresh();
        }
    }

    private void AddItems(ChatMessage message, IReadOnlyList<ChatItem> items)
    {
        foreach (var item in items)
            message.Items.Add(new ChatItemRow(item, this, item.EntryId is long id ? _findBasketRow(id) : null));
    }

    private static string Credit(ChatAnswer a)
    {
        string looked = a.Lookups.Count > 0 ? $"Looked up: {string.Join("; ", a.Lookups)}. " : "";
        string time = a.Elapsed.TotalSeconds >= 1 ? $" ({a.Elapsed.TotalSeconds:0} s)" : "";
        string credit = a.Source switch
        {
            AnswerSource.Scan => $"{looked}Answered straight from the scan{time}.",
            AnswerSource.ScanUnsure => $"{looked}Answered straight from the scan{time}.",
            AnswerSource.AiUnderstood => $"{looked}{a.ModelName} worked out what you meant, on this PC{time}; the answer itself comes straight from the scan.",
            AnswerSource.LocalAi => $"{looked}Worded by {a.ModelName} on this PC and checked against the scan{time}.",
            AnswerSource.LocalAiFallback => $"{looked}{a.ModelName}'s wording didn't match the scan exactly, so this is the plain answer from the scan{time}.",
            _ => "",
        };
        return a.Note is { Length: > 0 } note ? (credit + " " + note).Trim() : credit;
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void ClearChat()
    {
        _cts?.Cancel();
        Messages.Clear();
        _engine?.Reset();
        OnPropertyChanged(nameof(HasMessages));
    }

    public void Dispose() => Close();
}
