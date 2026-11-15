using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PuterScanny.AI;
using PuterScanny.Core.Models;

namespace PuterScanny.App.ViewModels;

/// <summary>One model in the "Local AI" window.</summary>
public sealed partial class ModelRow(ModelInfo model) : ObservableObject
{
    public ModelInfo Model { get; } = model;
    public string Name => Model.DisplayName;
    public string Description => Model.Description;
    public string Facts => $"{Display.Size(Model.SizeBytes)} download · {Model.License} licence (free) · {Model.Source}";

    [ObservableProperty] public partial string StateText { get; set; } = "";
    [ObservableProperty] public partial bool IsInUse { get; set; }
    [ObservableProperty] public partial bool CanDownload { get; set; }
    [ObservableProperty] public partial bool CanDelete { get; set; }
    [ObservableProperty] public partial bool CanUse { get; set; }
    [ObservableProperty] public partial string DownloadLabel { get; set; } = "Download";
}

/// <summary>
/// The local AI as the app sees it: settings, model downloads, and explaining items (one at a time when selected,
/// or the biggest unidentified ones in a batch).
/// </summary>
public sealed partial class AiController : ObservableObject, IDisposable
{
    private const int BatchSize = 50;
    private const long BatchMinBytes = 1L << 20;

    private readonly Func<ResultsContext?> _results;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _batchCts;

    public AiController(Func<ResultsContext?> results)
    {
        _results = results;
        Service = new ExplanationService(new AiPaths());
        Service.StatusChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        foreach (var m in ModelCatalog.All) Models.Add(new ModelRow(m));
        Refresh();
    }

    public ExplanationService Service { get; }
    public ObservableCollection<ModelRow> Models { get; } = [];

    /// <summary>Raised (on the UI thread) when a new explanation was written, so open panels can show it.</summary>
    public event EventHandler? ExplanationsChanged;

    [ObservableProperty] public partial string StatusText { get; set; } = "";

    /// <summary>What this PC allows right now ("Fast enough for summaries…"), with its memory and processor.</summary>
    [ObservableProperty] public partial string CapacityText { get; set; } = "";
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial string DownloadText { get; set; } = "";
    [ObservableProperty] public partial double DownloadPercent { get; set; }
    [ObservableProperty] public partial bool IsBatchRunning { get; set; }
    [ObservableProperty] public partial string BatchText { get; set; } = "";
    [ObservableProperty] public partial string CustomModelText { get; set; } = "";
    [ObservableProperty] public partial string CacheText { get; set; } = "";

    public bool IsAvailable => Service.IsAvailable;
    public bool HasResults => _results() is not null;

    public string ModelsFolder => Service.Paths.Models;

    public bool Enabled
    {
        get => Service.Settings.Enabled;
        set
        {
            if (Service.Settings.Enabled == value) return;
            Service.Settings.Enabled = value;
            Service.SaveSettings();
            OnPropertyChanged();
            Refresh();
        }
    }

    public bool AutoExplain
    {
        get => Service.Settings.AutoExplain;
        set
        {
            if (Service.Settings.AutoExplain == value) return;
            Service.Settings.AutoExplain = value;
            Service.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool GentleOnPc
    {
        get => Service.GentleOnPc;
        set { Service.GentleOnPc = value; OnPropertyChanged(); }
    }

    /// <summary>"Qwen3.5 4B" (for the "AI-written by…" line).</summary>
    public string ModelName => Service.ResolveModel()?.Name ?? "the local AI";

    public void Refresh()
    {
        var resolved = Service.ResolveModel();
        foreach (var row in Models)
        {
            var state = ModelDownloader.GetState(row.Model, ModelsFolder);
            bool inUse = string.IsNullOrWhiteSpace(Service.Settings.CustomModelPath) && Service.Settings.ModelId == row.Model.Id;
            row.IsInUse = inUse && state == ModelState.Ready;
            row.StateText = state switch
            {
                ModelState.Ready => inUse ? "Downloaded · in use" : "Downloaded",
                ModelState.NeedsCheck => "Downloaded, not checked yet",
                ModelState.Partial => $"Partly downloaded ({Display.Size(ModelDownloader.PartialBytes(row.Model, ModelsFolder))} of {Display.Size(row.Model.SizeBytes)})",
                _ => "Not downloaded",
            };
            row.CanDownload = !IsDownloading && state != ModelState.Ready;
            row.DownloadLabel = state switch { ModelState.Partial => "Resume download", ModelState.NeedsCheck => "Check file", _ => "Download" };
            row.CanDelete = !IsDownloading && state != ModelState.NotDownloaded;
            row.CanUse = state == ModelState.Ready && !row.IsInUse;
        }

        CustomModelText = string.IsNullOrWhiteSpace(Service.Settings.CustomModelPath)
            ? "Not used."
            : File.Exists(Service.Settings.CustomModelPath)
                ? $"In use: {Service.Settings.CustomModelPath}"
                : $"Not found: {Service.Settings.CustomModelPath}";

        StatusText = Service.Status switch
        {
            AiStatus.NotSetUp when !Service.Settings.Enabled => "Off. The app answers from the scan alone and never uses the AI.",
            AiStatus.NotSetUp => "Automatic, but no model is downloaded yet. Download one below (one time, free, no account).",
            AiStatus.Ready => $"Ready: {resolved?.Name}. It loads into memory the first time it's needed.",
            AiStatus.Loading => $"Loading {resolved?.Name} into memory…",
            AiStatus.Loaded => $"Ready: {resolved?.Name} is loaded.",
            AiStatus.Failed => Service.Problem ?? "The model couldn't be loaded.",
            _ => "",
        };
        CapacityText = CapacityDescription();
        try { CacheText = $"{Display.Count(Service.CachedCount, "explanation", "explanations")} saved on this PC."; }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { CacheText = ""; }

        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(HasResults));
        ExplainBiggestCommand.NotifyCanExecuteChanged();
    }

    private string CapacityDescription()
    {
        var c = Service.Capacity();
        if (c.Tier == AiTier.Off) return "";
        static string Gb(long b) => Display.Size(b);
        string pc = $"This PC: {Gb(c.TotalMemory)} of memory ({Gb(c.FreeMemory)} free), {Environment.ProcessorCount} processor threads" +
                    (c.Avx2 ? "" : ", an older processor (no AVX2)") + (c.OnBattery ? ", on battery" : "") +
                    (c.TokensPerSecond is double t ? $", about {t:0.#} words a second measured" : "") + ".";
        string use = c.Tier switch
        {
            AiTier.Fast when c.Summaries => "Used to understand questions the app can't read on its own, and to add short summaries to answers.",
            AiTier.Fast => "Used to understand questions the app can't read on its own. Summaries are skipped for now.",
            AiTier.Slow => "Used only to understand questions the app can't read on its own (no summaries).",
            _ => "Not used for now.",
        };
        return $"{pc} {use} Why: {c.Reason}.";
    }

    // ---------------------------------------------------------------- models

    [RelayCommand]
    private async Task DownloadAsync(ModelRow? row)
    {
        if (row is null || IsDownloading) return;
        var m = row.Model;
        if (ModelDownloader.GetState(m, ModelsFolder) == ModelState.NotDownloaded &&
            MessageBox.Show($"Download {m.DisplayName}?\n\nFile: {m.FileName}\nSize: {Display.Size(m.SizeBytes)}\nFrom: {new Uri(m.Url).Host} ({m.Source})\nLicence: {m.License} (free to use)\nSaved to: {ModelsFolder}\n\n" +
                            "It's downloaded once and then runs entirely on this PC: nothing about your files is ever sent anywhere.",
                            "PuterScanny – Local AI", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        DownloadPercent = 0;
        DownloadText = "Connecting…";
        Refresh();
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadPercent = p.Percent;
                DownloadText = p.Stage == "Checking"
                    ? $"Checking the file is complete and genuine… {p.Percent:F0}%"
                    : $"Downloading {m.DisplayName}: {Display.Size(p.Done)} of {Display.Size(p.Total)} ({p.Percent:F0}%)" +
                      (p.BytesPerSecond > 0 ? $" · {Display.Size((long)p.BytesPerSecond)}/s · about {Remaining(p)} left" : "");
            });
            await ModelDownloader.DownloadAsync(m, ModelsFolder, progress, _downloadCts.Token);
            DownloadText = $"{m.DisplayName} is downloaded and checked.";
            if (string.IsNullOrWhiteSpace(Service.Settings.CustomModelPath) || Service.ResolveModel() is null)
            {
                Service.Settings.ModelId = m.Id;
                Service.Settings.CustomModelPath = null;
                Service.Settings.Enabled = true;
                Service.SaveSettings();
                OnPropertyChanged(nameof(Enabled));
            }
        }
        catch (OperationCanceledException)
        {
            DownloadText = "Download paused. Press \"Resume download\" to continue where it stopped.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DownloadText = ex is HttpRequestException
                ? $"The download failed: {ex.Message} Check the internet connection and try again (it continues where it stopped)."
                : ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
            Refresh();
        }
    }

    private static string Remaining(DownloadProgress p)
    {
        var t = TimeSpan.FromSeconds((p.Total - p.Done) / Math.Max(1, p.BytesPerSecond));
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(1, (int)t.TotalSeconds)} s";
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private void UseModel(ModelRow? row)
    {
        if (row is null) return;
        Service.Settings.ModelId = row.Model.Id;
        Service.Settings.CustomModelPath = null;
        Service.SaveSettings();
        Refresh();
    }

    [RelayCommand]
    private void DeleteModel(ModelRow? row)
    {
        if (row is null) return;
        if (MessageBox.Show($"Delete {row.Model.DisplayName} from this PC to free {Display.Size(row.Model.SizeBytes)}?\n\nExplanations already written are kept. You can download it again any time.",
                "PuterScanny – Local AI", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Service.Unload();
        try
        {
            // The model may still be closing in the background; give it a moment.
            for (int attempt = 0; ; attempt++)
            {
                try { ModelDownloader.Delete(row.Model, ModelsFolder); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"The model file couldn't be deleted: {ex.Message}", "PuterScanny – Local AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    [RelayCommand]
    private void ChooseOwnModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a GGUF model file",
            Filter = "GGUF model (*.gguf)|*.gguf",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        Service.Settings.CustomModelPath = dialog.FileName;
        Service.Settings.Enabled = true;
        Service.SaveSettings();
        OnPropertyChanged(nameof(Enabled));
        Refresh();
    }

    [RelayCommand]
    private void StopUsingOwnModel()
    {
        Service.Settings.CustomModelPath = null;
        Service.SaveSettings();
        Refresh();
    }

    [RelayCommand]
    private void ClearSaved()
    {
        if (MessageBox.Show("Forget every explanation written so far? They'll be written again when needed.", "PuterScanny – Local AI",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Service.ClearCache();
        Refresh();
        ExplanationsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void FreeMemory() => Service.Unload();

    [RelayCommand]
    private void OpenModelsFolder()
    {
        Directory.CreateDirectory(ModelsFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ModelsFolder}\"") { UseShellExecute = true });
    }

    // ---------------------------------------------------------------- explaining

    /// <summary>Explains one item (cache first). Returns null when the model had no usable answer.</summary>
    public async Task<Explanation?> ExplainAsync(FileSystemEntry e, ResultsContext ctx, CancellationToken ct)
    {
        var facts = AiFactsBuilder.Build(e, ctx);
        var result = await Service.ExplainAsync(facts, e.Path, ct);
        if (result is { FromCache: false }) Application.Current?.Dispatcher.BeginInvoke(() => Refresh());
        return result;
    }

    public Explanation? GetCached(FileSystemEntry e, ResultsContext ctx)
    {
        try { return Service.GetCached(AiFactsBuilder.Build(e, ctx)); }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    private bool CanExplainBiggest() => !IsBatchRunning && Service.IsAvailable && _results() is not null;

    [RelayCommand(CanExecute = nameof(CanExplainBiggest))]
    private async Task ExplainBiggestAsync()
    {
        if (_results() is not { } ctx) return;
        var items = ctx.Db.GetUnidentifiedItems(BatchSize, BatchMinBytes);
        if (items.Count == 0)
        {
            BatchText = "Nothing to explain: every item over 1 MB in this scan was identified by PuterScanny's own rules.";
            return;
        }

        IsBatchRunning = true;
        ExplainBiggestCommand.NotifyCanExecuteChanged();
        _batchCts = new CancellationTokenSource();
        int done = 0, written = 0, failed = 0;
        try
        {
            foreach (var e in items)
            {
                _batchCts.Token.ThrowIfCancellationRequested();
                BatchText = $"Explaining {done + 1} of {items.Count}: {e.Name}";
                try
                {
                    var result = await ExplainAsync(e, ctx, _batchCts.Token);
                    if (result is null) failed++;
                    else if (!result.FromCache) written++;
                }
                catch (InvalidOperationException ex)
                {
                    BatchText = ex.Message;
                    return;
                }
                done++;
                if (written > 0 && written % 5 == 0) ExplanationsChanged?.Invoke(this, EventArgs.Empty);
            }
            BatchText = $"Done: the {items.Count} biggest unidentified items are explained ({written} new{(failed > 0 ? $", {failed} without a usable answer" : "")}). Select one to read it.";
        }
        catch (OperationCanceledException)
        {
            BatchText = $"Stopped after {done} of {items.Count}. Already written explanations are kept.";
        }
        finally
        {
            IsBatchRunning = false;
            _batchCts.Dispose();
            _batchCts = null;
            ExplainBiggestCommand.NotifyCanExecuteChanged();
            ExplanationsChanged?.Invoke(this, EventArgs.Empty);
            Refresh();
        }
    }

    [RelayCommand]
    private void StopBatch() => _batchCts?.Cancel();

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _batchCts?.Cancel();
        Service.Dispose();
    }
}
