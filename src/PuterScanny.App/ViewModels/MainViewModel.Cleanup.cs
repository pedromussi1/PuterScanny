using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PuterScanny.Cleanup;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Scanning;

namespace PuterScanny.App.ViewModels;

/// <summary>The clean-up basket, running a confirmed clean-up, and undo / quarantine management.</summary>
public sealed partial class MainViewModel
{
    private CleanupHistory? _history;
    private readonly Quarantine _quarantine = new();

    public ObservableCollection<BatchRow> Batches { get; } = [];
    public ObservableCollection<BatchItemRow> BatchItems { get; } = [];

    /// <summary>Set by the window: shows the confirmation dialog, returns null when cancelled.</summary>
    public Func<IReadOnlyList<CleanupRow>, (RemovalMethod Method, bool RestorePoint)?>? ConfirmCleanup { get; set; }

    [ObservableProperty] public partial string BasketText { get; set; } = "Tick items to put them in the clean-up basket.";
    [ObservableProperty] public partial string QuarantineText { get; set; } = "";
    [ObservableProperty] public partial string FreedText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanUpSelectedCommand), nameof(UndoBatchCommand), nameof(EmptyQuarantineCommand))]
    public partial bool IsCleaning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoBatchCommand))]
    public partial BatchRow? SelectedBatch { get; set; }

    private CleanupHistory History => _history ??= new CleanupHistory();

    private IEnumerable<CleanupRow> AllRows => DeletableItems.Concat(ReviewItems);

    private List<CleanupRow> Basket() => AllRows.Where(r => r.IsChecked && r.CanSelect).ToList();

    private void UpdateBasket()
    {
        var basket = Basket();
        BasketText = basket.Count == 0
            ? "Tick items to put them in the clean-up basket."
            : $"In the basket: {basket.Count:N0} item{(basket.Count == 1 ? "" : "s")}, {Display.Size(basket.Sum(r => r.Size))}";
        CleanUpSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var r in DeletableItems.Where(r => r.VerdictValue == Verdict.RecommendDelete && r.CanSelect)) r.IsChecked = true;
    }

    [RelayCommand]
    private void SelectAllDeletable()
    {
        foreach (var r in DeletableItems.Where(r => r.CanSelect)) r.IsChecked = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in AllRows) r.IsChecked = false;
    }

    private bool CanCleanUp() => !IsCleaning && !IsScanning && _ctx is not null && Basket().Count > 0;

    [RelayCommand(CanExecute = nameof(CanCleanUp))]
    private async Task CleanUpSelectedAsync()
    {
        if (_ctx is null) return;
        var basket = Basket();
        if (basket.Count == 0 || ConfirmCleanup?.Invoke(basket) is not { } choice) return;

        IsCleaning = true;
        string previous = StatusText;
        try
        {
            if (choice.RestorePoint)
            {
                StatusText = "Creating a restore point…";
                var (ok, message) = await Task.Run(() => RestorePoint.TryCreate("Before PuterScanny clean-up"));
                if (!ok && MessageBox.Show($"{message}\n\nContinue with the clean-up anyway? It can still be undone from PuterScanny.",
                        "PuterScanny", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            var items = basket.Select(r => new CleanupItem(r.EntryId, r.Path, r.IsFolder, r.Size, r.ModifiedUtc, r.FileCount, r.VerdictValue, r.ActionValue, r.Why)).ToList();
            var safety = new SafetyChecks(PathTokens.ForThisMachine(), [ScanDatabaseFolder()]);
            var runner = new CleanupRunner(safety, _quarantine, History);
            var progress = new Progress<CleanupProgress>(p => StatusText = p.Current.Length == 0 ? "Finishing…" : $"Cleaning up {p.Done + 1:N0} of {p.Total:N0}: {p.Current}");
            string dbPath = _ctx.Db.FilePath;
            var batch = await Task.Run(() => runner.Run(items, choice.Method, dbPath, progress, CancellationToken.None));

            var removed = batch.Items.Where(i => i.Status == ItemStatus.Removed).ToList();
            _ctx.Db.MarkRemoved(removed.Select(i => i.EntryId), batch.Id, (int)choice.Method);
            var gone = removed.Select(i => i.OriginalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in AllRows.Where(r => gone.Contains(r.Path)).ToList())
            {
                DeletableItems.Remove(row);
                ReviewItems.Remove(row);
            }
            UpdateBasket();
            LoadHistory();

            var skipped = batch.Items.Where(i => i.Status is ItemStatus.Skipped or ItemStatus.Failed).ToList();
            string where = choice.Method == RemovalMethod.RecycleBin ? "the Recycle Bin" : "PuterScanny's quarantine";
            string text = $"Moved {removed.Count:N0} item{(removed.Count == 1 ? "" : "s")} ({Display.Size(removed.Sum(i => i.Size))}) to {where}.";
            if (removed.Any(i => i.Method == RemovalMethod.RecycleBin && i.Note.Contains("quarantine", StringComparison.Ordinal)))
                text += "\nSome items couldn't go in the Recycle Bin (path too long or too big), so they were put in quarantine instead.";
            if (skipped.Count > 0)
                text += $"\n\nLeft alone ({skipped.Count:N0}):\n" + string.Join("\n", skipped.Take(8).Select(s => $"• {Path.GetFileName(s.OriginalPath)}: {s.Note}"))
                        + (skipped.Count > 8 ? $"\n… and {skipped.Count - 8:N0} more (see Undo & quarantine)." : "");
            text += "\n\nYou can undo this any time from the Undo & quarantine tab.";
            StatusText = $"Cleaned up {Display.Size(removed.Sum(i => i.Size))}. Undo from the Undo & quarantine tab.";
            MessageBox.Show(text, "Clean-up finished", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = previous;
            MessageBox.Show($"The clean-up stopped:\n\n{ex.Message}\n\nEverything moved so far is listed in the Undo & quarantine tab.",
                "PuterScanny", MessageBoxButton.OK, MessageBoxImage.Error);
            LoadHistory();
        }
        finally
        {
            IsCleaning = false;
        }
    }

    private static string ScanDatabaseFolder() => Path.GetDirectoryName(PuterScanny.Storage.ScanDatabase.DefaultScansFolder)!;

    // ---------------------------------------------------------------- undo & quarantine

    public void LoadHistory()
    {
        Batches.Clear();
        foreach (var b in History.Batches)
        {
            int removed = b.Items.Count(i => i.Status == ItemStatus.Removed);
            int restored = b.Items.Count(i => i.Status == ItemStatus.Restored);
            int purged = b.Items.Count(i => i.Status == ItemStatus.Purged);
            int skipped = b.Items.Count(i => i.Status is ItemStatus.Skipped or ItemStatus.Failed);
            var parts = new List<string>();
            if (removed > 0) parts.Add($"{removed:N0} can be undone");
            if (restored > 0) parts.Add($"{restored:N0} restored");
            if (purged > 0) parts.Add($"{purged:N0} deleted for good");
            if (skipped > 0) parts.Add($"{skipped:N0} left alone");
            Batches.Add(new BatchRow(b.Id, b.StartedUtc.ToLocalTime().ToString("g"),
                b.Method == RemovalMethod.RecycleBin ? "Recycle Bin" : "Quarantine",
                string.Join(" · ", parts), Display.Size(b.Items.Where(i => i.Status == ItemStatus.Removed).Sum(i => i.Size)),
                removed > 0, removed > 0 ? "Can be undone" : "Done"));
        }
        var runner = new CleanupRunner(new SafetyChecks(PathTokens.ForThisMachine()), _quarantine, History);
        long quarantined = runner.QuarantinedBytes();
        QuarantineText = quarantined > 0
            ? $"{Display.Size(quarantined)} is in quarantine. It still takes space until you empty it."
            : "The quarantine is empty.";
        long freed = History.Batches.SelectMany(b => b.Items).Where(i => i.Status is ItemStatus.Removed or ItemStatus.Purged).Sum(i => i.Size);
        FreedText = freed > 0 ? $"Cleaned up so far: {Display.Size(freed)}" : "";
        EmptyQuarantineCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBatchChanged(BatchRow? value)
    {
        BatchItems.Clear();
        if (value is null || History.Find(value.Id) is not { } batch) return;
        foreach (var i in batch.Items)
        {
            string status = i.Status switch
            {
                ItemStatus.Removed => i.Method == RemovalMethod.RecycleBin && i.StoredAt is not null && !_quarantine.IsInsideQuarantine(i.StoredAt) ? "In the Recycle Bin" : "In quarantine",
                ItemStatus.Restored => "Restored",
                ItemStatus.Purged => "Deleted for good",
                ItemStatus.Skipped => "Left alone",
                _ => "Problem",
            };
            BatchItems.Add(new BatchItemRow(i.OriginalPath, Display.Size(i.Size), status, i.Note));
        }
    }

    private bool CanUndo(BatchRow? row) => !IsCleaning && (row ?? SelectedBatch) is { CanUndo: true };

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoBatchAsync(BatchRow? row)
    {
        row ??= SelectedBatch;
        if (row is null || History.Find(row.Id) is not { } batch) return;
        if (MessageBox.Show($"Put back everything from the clean-up of {row.When} that's still in the {row.Method}?", "Undo clean-up",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        IsCleaning = true;
        try
        {
            var runner = new CleanupRunner(new SafetyChecks(PathTokens.ForThisMachine()), _quarantine, History);
            var undone = batch.Items.Where(i => i.Status == ItemStatus.Removed).Select(i => i.EntryId).ToList();
            var (restored, problems, messages) = await Task.Run(() => runner.Undo(batch.Id));
            if (_ctx is not null && string.Equals(batch.ScanDatabase, _ctx.Db.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _ctx.Db.UnmarkRemoved(undone);
                DeletableItems.Clear();
                ReviewItems.Clear();
                ToolItems.Clear();
                UninstallItems.Clear();
                Duplicates.Clear();
                LoadCleanup(_ctx.Db);
            }
            LoadHistory();
            string text = $"Restored {restored:N0} item{(restored == 1 ? "" : "s")}.";
            if (problems > 0 || messages.Count > 0) text += "\n\n" + string.Join("\n", messages.Take(10));
            MessageBox.Show(text, "Undo finished", MessageBoxButton.OK, problems > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    private bool CanEmptyQuarantine(string? _) => !IsCleaning && QuarantineText.Contains("in quarantine", StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanEmptyQuarantine))]
    private async Task EmptyQuarantineAsync(string? olderThanDays)
    {
        int days = int.TryParse(olderThanDays, out var d) ? d : 0;
        string which = days > 0 ? $"items that have been in quarantine for more than {days} days" : "everything in quarantine";
        if (MessageBox.Show($"Permanently delete {which}?\n\nThis frees the space, but those items can't be put back afterwards.",
                "Empty quarantine", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        IsCleaning = true;
        try
        {
            var runner = new CleanupRunner(new SafetyChecks(PathTokens.ForThisMachine()), _quarantine, History);
            var (purged, bytes) = await Task.Run(() => runner.EmptyQuarantine(days));
            LoadHistory();
            MessageBox.Show(purged == 0 ? "Nothing needed deleting." : $"Deleted {purged:N0} item{(purged == 1 ? "" : "s")} for good, freeing {Display.Size(bytes)}.",
                "Empty quarantine", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    partial void OnQuarantineTextChanged(string value) => EmptyQuarantineCommand.NotifyCanExecuteChanged();

    public bool IsAdmin => ElevationInfo.IsProcessElevated();
}
