using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuterScanny.AI;
using PuterScanny.Core.Models;

namespace PuterScanny.App.ViewModels;

/// <summary>
/// The "What the local AI thinks this is" box in a details panel. Shown for items PuterScanny couldn't identify
/// (explained automatically when that's turned on), or for any item on request.
/// </summary>
public sealed partial class AiExplainBlock : ObservableObject
{
    private readonly AiController _ai;
    private readonly Func<ResultsContext?> _results;
    private FileSystemEntry? _entry;
    private CancellationTokenSource? _cts;

    public AiExplainBlock(AiController ai, Func<ResultsContext?> results)
    {
        _ai = ai;
        _results = results;
        _ai.ExplanationsChanged += (_, _) => { if (!IsBusy && Text is null) Show(_entry, autoStart: false); };
        _ai.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AiController.IsAvailable)) Update(); };
    }

    [ObservableProperty] public partial string? Text { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Credit { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial bool NeedsSetup { get; set; }
    [ObservableProperty] public partial bool IsUnidentified { get; set; }

    /// <summary>The small "Explain with local AI" button under an identified item (the box itself is hidden).</summary>
    [ObservableProperty] public partial bool ShowAskButton { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExplainCommand))]
    public partial bool CanAsk { get; set; }

    /// <summary>Called when the panel's selection changes.</summary>
    public void Show(FileSystemEntry? entry, bool autoStart = true)
    {
        if (!ReferenceEquals(entry, _entry))
        {
            _cts?.Cancel();
            _cts = null;
            IsBusy = false;
        }
        _entry = entry;
        Text = null;
        Status = "";
        Credit = "";
        IsUnidentified = entry is not null && AiTargets.NeedsExplanation(entry);

        if (entry is not null && _results() is { } ctx && _ai.IsAvailable && _ai.GetCached(entry, ctx) is { } cached)
            SetResult(cached);
        else if (autoStart && IsUnidentified && _ai.IsAvailable && _ai.AutoExplain)
            _ = ExplainAsync();
        Update();
    }

    private void Update()
    {
        bool available = _ai.IsAvailable;
        if (Text is null && !IsBusy && Status.Length == 0 && IsUnidentified)
        {
            Status = available
                ? "PuterScanny's own rules couldn't identify this item. The local AI can suggest what it probably is."
                : "PuterScanny's own rules couldn't identify this item. Turn on the free local AI to get a plain-English guess at what it is.";
        }
        NeedsSetup = !available && IsUnidentified;
        CanAsk = _entry is not null && available && !IsBusy && Text is null;
        IsVisible = _entry is not null && (Text is not null || IsBusy || IsUnidentified || Status.Length > 0);
        ShowAskButton = CanAsk && !IsVisible;
    }

    private bool CanExplain() => CanAsk;

    [RelayCommand(CanExecute = nameof(CanExplain))]
    private async Task ExplainAsync()
    {
        if (_entry is not { } entry || _results() is not { } ctx || IsBusy) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Status = _ai.Service.Status == AiStatus.Loaded
            ? "The local AI is thinking (usually 5–30 seconds)…"
            : "Loading the local AI into memory (first time only), then thinking…";
        Update();
        try
        {
            var result = await _ai.ExplainAsync(entry, ctx, token);
            if (!ReferenceEquals(entry, _entry)) return;
            if (result is null) Status = "The local AI couldn't give a useful answer for this item.";
            else SetResult(result);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex)
        {
            if (ReferenceEquals(entry, _entry)) Status = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(entry, _entry))
            {
                IsBusy = false;
                Update();
            }
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        IsBusy = false;
        Status = "Stopped.";
        Update();
    }

    private void SetResult(Explanation e)
    {
        Text = e.Text;
        Status = "";
        Credit = $"AI-written by {e.ModelName} on this PC{(e.FromCache ? $" ({Display.Ago(e.CreatedUtc)})" : "")}. It can be wrong, and it never changes PuterScanny's recommendation.";
        Update();
    }
}
