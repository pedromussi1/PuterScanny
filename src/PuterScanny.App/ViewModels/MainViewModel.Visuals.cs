using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PuterScanny.Analysis.Reporting;
using PuterScanny.App.Controls;
using PuterScanny.Core.Models;
using PuterScanny.Storage;

namespace PuterScanny.App.ViewModels;

public sealed record StatCard(string Number, string Label);

public sealed record LegendItem(Brush Fill, string Label);

public sealed record BarRow(string Name, string SizeText, double Percent, string Note, string Path);

public sealed record Crumb(string Name, FileSystemEntry Entry);

/// <summary>Overview dashboard, treemap and report export.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<LegendItem> VerdictLegend { get; } = [];
    public ObservableCollection<BarRow> TopCleanup { get; } = [];
    public ObservableCollection<BarRow> TopFolders { get; } = [];
    public ObservableCollection<BarRow> TopKinds { get; } = [];
    public ObservableCollection<string> HeadsUp { get; } = [];
    public ObservableCollection<Crumb> Crumbs { get; } = [];
    public ObservableCollection<DetailRow> TreemapDetails { get; } = [];
    public ObservableCollection<LegendItem> TreemapLegend { get; } = [];

    [ObservableProperty] public partial IReadOnlyList<(double Value, Brush Fill)>? VerdictSegments { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TreemapNode>? TreemapNodes { get; set; }
    [ObservableProperty] public partial TreemapNode? TreemapSelected { get; set; }
    [ObservableProperty] public partial bool TreemapByKind { get; set; }
    [ObservableProperty] public partial string TreemapTitle { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; set; }

    // ---------------------------------------------------------------- dashboard

    private void LoadDashboard(ScanDatabase db, ScanSummary summary, FileSystemEntry root)
    {
        long recommend = 0, safe = 0, review = 0, tool = 0, required = 0, keep = 0, personal = 0;
        var segments = new List<(double, Brush)>();
        foreach (var (verdict, action, _, bytes) in db.GetVerdictStats())
        {
            if (action == CleanupAction.UseWindowsTool) tool += bytes;
            else if (verdict == Verdict.RecommendDelete) recommend += bytes;
            else if (verdict == Verdict.SafeToDelete) safe += bytes;
            else if (verdict == Verdict.Review) review += bytes;
            else if (verdict == Verdict.Required) required += bytes;
            else if (verdict == Verdict.KeepPersonal) personal += bytes;
            else keep += bytes;
        }

        Cards.Clear();
        Cards.Add(new StatCard(Display.Size(summary.TotalSize), $"scanned in {Display.Count(summary.FileCount, "file", "files")}"));
        Cards.Add(new StatCard(Display.Size(recommend + safe), "can be freed safely"));
        Cards.Add(new StatCard(Display.Size(review), "worth reviewing"));
        Cards.Add(new StatCard(Display.Size(tool), "with Windows tools"));
        Cards.Add(new StatCard(Display.Size(Duplicates.Sum(d => d.Wasted)), "in duplicate copies"));
        Cards.Add(new StatCard(UninstallItems.Count.ToString("N0"), "programs you might uninstall"));
        Cards.Add(new StatCard(Display.Size(required), "Windows needs"));

        var parts = new (long Bytes, Brush Fill, string Label)[]
        {
            (required, Palette.Required, "Required by Windows"),
            (keep, Palette.Keep, "Keep"),
            (personal, Palette.Personal, "Your files"),
            (review, Palette.Review, "Review"),
            (tool, Palette.Tool, "Windows tools"),
            (safe, Palette.Safe, "Safe to delete"),
            (recommend, Palette.Recommend, "Recommended to delete"),
        };
        VerdictSegments = parts.Where(p => p.Bytes > 0).Select(p => ((double)p.Bytes, p.Fill)).ToList();
        VerdictLegend.Clear();
        foreach (var p in parts.Where(p => p.Bytes > 0)) VerdictLegend.Add(new LegendItem(p.Fill, $"{p.Label}: {Display.Size(p.Bytes)}"));

        TopCleanup.Clear();
        long maxClean = DeletableItems.Count == 0 ? 1 : Math.Max(1, DeletableItems.Max(d => d.Size));
        foreach (var d in DeletableItems.Take(8)) TopCleanup.Add(new BarRow(d.Name, d.SizeText, d.Size * 100.0 / maxClean, d.Why, d.Path));

        TopFolders.Clear();
        long rootSize = Math.Max(1, root.TotalSize);
        foreach (var c in db.GetChildren(root.Id, 8))
        {
            string note = c.Analysis is { ReclaimBytes: > 0 } a ? $"{Display.Size(a.ReclaimBytes)} can go" : c.Analysis?.Headline ?? "";
            TopFolders.Add(new BarRow(c.Name, Display.Size(c.TotalSize), c.TotalSize * 100.0 / rootSize, note, c.Path));
        }

        TopKinds.Clear();
        double maxKind = Categories.Count == 0 ? 1 : Math.Max(1, Categories.Max(c => c.Bytes));
        foreach (var c in Categories.Take(8)) TopKinds.Add(new BarRow(c.Name, c.SizeText, c.Bytes * 100.0 / maxKind, $"{c.Files:N0} files", ""));

        HeadsUp.Clear();
        if (summary.Status != ScanStatus.Completed) HeadsUp.Add("The scan didn't finish, so these numbers are incomplete.");
        if (AnalysisNote.Length > 0) HeadsUp.Add(AnalysisNote);
        if (summary.IssueCount > 0) HeadsUp.Add($"{summary.IssueCount:N0} places couldn't be fully looked inside (see Skipped & issues).");
        foreach (var s in UsageSources.Where(s => s.Status != "Checked")) HeadsUp.Add($"Not checked: {s.Source}. {s.Note}");
        if (HeadsUp.Count == 0) HeadsUp.Add("Everything that could be checked was checked.");
    }

    // ---------------------------------------------------------------- treemap

    private readonly List<FileSystemEntry> _treemapStack = [];

    private void InitTreemap(FileSystemEntry root)
    {
        _treemapStack.Clear();
        _treemapStack.Add(root);
        BuildTreemap();
    }

    partial void OnTreemapByKindChanged(bool value) => BuildTreemap();

    partial void OnTreemapSelectedChanged(TreemapNode? value)
    {
        TreemapDetails.Clear();
        TreemapAi.Show(_ctx is null ? null : value?.Tag as FileSystemEntry);
        if (value?.Tag is FileSystemEntry e && _ctx is not null)
            foreach (var row in DetailsBuilder.Build(e, _ctx)) TreemapDetails.Add(row);
    }

    public void OpenTreemapNode(TreemapNode node)
    {
        if (node.Tag is not FileSystemEntry { IsDirectory: true } e) return;
        _treemapStack.Add(e);
        BuildTreemap();
    }

    [RelayCommand]
    private void TreemapUp()
    {
        if (_treemapStack.Count <= 1) return;
        _treemapStack.RemoveAt(_treemapStack.Count - 1);
        BuildTreemap();
    }

    [RelayCommand]
    private void TreemapGoTo(Crumb? crumb)
    {
        if (crumb is null) return;
        int i = _treemapStack.IndexOf(crumb.Entry);
        if (i < 0) return;
        _treemapStack.RemoveRange(i + 1, _treemapStack.Count - i - 1);
        BuildTreemap();
    }

    private void BuildTreemap()
    {
        if (_ctx is null || _treemapStack.Count == 0) return;
        var folder = _treemapStack[^1];
        var db = _ctx.Db;

        var nodes = new List<TreemapNode>();
        var kids = db.GetChildren(folder.Id, 150);
        long shown = 0;
        foreach (var kid in kids)
        {
            if (kid.TotalSize <= 0) continue;
            shown += kid.TotalSize;
            var inner = new List<TreemapNode>();
            if (kid.IsDirectory)
            {
                long innerShown = 0;
                foreach (var g in db.GetChildren(kid.Id, 40).Where(g => g.TotalSize > 0))
                {
                    inner.Add(Node(g, withChildren: []));
                    innerShown += g.TotalSize;
                }
                if (inner.Count > 0 && kid.TotalSize - innerShown > kid.TotalSize / 100)
                    inner.Add(Rest(kid.TotalSize - innerShown));
            }
            nodes.Add(Node(kid, inner));
        }
        if (folder.TotalSize - shown > folder.TotalSize / 200) nodes.Add(Rest(folder.TotalSize - shown));

        TreemapNodes = nodes;
        TreemapSelected = null;
        TreemapTitle = $"{folder.Path}  ·  {Display.Size(folder.TotalSize)}";
        Crumbs.Clear();
        foreach (var e in _treemapStack) Crumbs.Add(new Crumb(e.Depth == 0 ? e.Path : e.Name, e));

        TreemapLegend.Clear();
        if (TreemapByKind)
        {
            foreach (var c in Categories.Take(10)) TreemapLegend.Add(new LegendItem(Palette.ForKind(KindId(c.Name)), c.Name));
            TreemapLegend.Add(new LegendItem(Palette.Unknown, "Folders / other"));
        }
        else
        {
            foreach (var (fill, label) in new[]
            {
                (Palette.Recommend, "Recommended to delete"), (Palette.Safe, "Safe to delete"), (Palette.Review, "Review"),
                (Palette.Tool, "Windows tools"), (Palette.Personal, "Your files"), (Palette.Keep, "Keep"), (Palette.Required, "Required by Windows"),
            }) TreemapLegend.Add(new LegendItem(fill, label));
        }
    }

    private string? KindId(string name) => _ctx?.Kb.Categories.FirstOrDefault(c => c.Name == name)?.Id;

    private TreemapNode Node(FileSystemEntry e, IReadOnlyList<TreemapNode> withChildren)
    {
        var a = e.Analysis;
        Brush fill;
        if (TreemapByKind) fill = e.IsDirectory ? Palette.Unknown : Palette.ForKind(a?.Category);
        else if (e.IsDirectory && a is not null && a.Verdict is not (Verdict.SafeToDelete or Verdict.RecommendDelete)
                 && e.TotalSize > 0 && a.ReclaimBytes * 2 >= e.TotalSize)
            fill = Palette.Safe; // mostly cleanable inside
        else fill = Palette.ForVerdict(a?.Verdict, a?.Action ?? CleanupAction.None);

        string verdict = a?.Verdict is { } v ? Display.VerdictTitle(v) : "";
        string reclaim = e.IsDirectory && a is { ReclaimBytes: > 0 } ? $"\n{Display.Size(a.ReclaimBytes)} inside can go" : "";
        return new TreemapNode
        {
            Label = $"{e.Name}\n{Display.Size(e.TotalSize)}",
            Size = e.TotalSize,
            Fill = fill,
            Tooltip = $"{e.Path}\n{Display.Size(e.TotalSize)}{(verdict.Length > 0 ? $"  ·  {verdict}" : "")}\n{a?.Headline}{reclaim}" +
                      (e.IsDirectory ? "\nDouble-click to open" : ""),
            Tag = e,
            CanOpen = e.IsDirectory && e.FileCount + e.DirectoryCount > 0,
            Children = withChildren,
        };
    }

    private static TreemapNode Rest(long bytes) => new()
    {
        Label = $"Smaller items\n{Display.Size(bytes)}",
        Size = bytes,
        Fill = Palette.Other,
        Tooltip = $"Many smaller items together: {Display.Size(bytes)}",
    };

    private void ClearVisuals()
    {
        Cards.Clear();
        VerdictLegend.Clear();
        TopCleanup.Clear();
        TopFolders.Clear();
        TopKinds.Clear();
        HeadsUp.Clear();
        Crumbs.Clear();
        TreemapDetails.Clear();
        TreemapLegend.Clear();
        VerdictSegments = null;
        TreemapNodes = null;
        _treemapStack.Clear();
    }

    // ---------------------------------------------------------------- export

    private bool CanExport() => HasResults && !IsExporting;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (_ctx is null) return;
        format ??= "html";
        var summary = _ctx.Db.GetSummary();
        string rootName = new string((summary?.RootPath ?? "scan").Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Trim();
        var dialog = new SaveFileDialog
        {
            Title = "Save report",
            FileName = $"PuterScanny report - {rootName} - {DateTime.Now:yyyy-MM-dd}",
            DefaultExt = "." + format,
            Filter = format switch
            {
                "csv" => "Spreadsheet (*.csv)|*.csv",
                "json" => "JSON data (*.json)|*.json",
                _ => "Web page report (*.html)|*.html",
            },
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true) return;

        IsExporting = true;
        string path = dialog.FileName;
        string previousStatus = StatusText;
        StatusText = "Writing the report…";
        try
        {
            var db = _ctx.Db;
            var kb = _ctx.Kb;
            if (format == "csv")
            {
                var progress = new Progress<long>(n => StatusText = $"Writing the spreadsheet… {n:N0} rows");
                long rows = await Task.Run(() => ReportExporters.WriteCsv(db, kb, path, progress));
                StatusText = $"Saved {rows:N0} rows to {path}";
            }
            else
            {
                var ai = Ai.Service;
                Func<string, string?>? aiNote = ai.Settings.Enabled ? p => ai.GetLatestForPath(p)?.Text : null;
                var report = await Task.Run(() => ReportData.From(db, kb, aiNote: aiNote));
                if (format == "json") await Task.Run(() => ReportExporters.WriteJson(report, path));
                else await Task.Run(() => ReportExporters.WriteHtml(report, path));
                StatusText = $"Saved the report to {path}";
            }

            if (MessageBox.Show($"Saved to:\n{path}\n\nOpen it now?", "Report saved", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = previousStatus;
            MessageBox.Show($"The report could not be saved:\n\n{ex.Message}", "PuterScanny", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsExporting = false;
        }
    }

    partial void OnHasResultsChanged(bool value) => ExportCommand.NotifyCanExecuteChanged();
}
