using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PuterScanny.Analysis;
using PuterScanny.Core.IO;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Scanning;
using PuterScanny.Storage;

namespace PuterScanny.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int LargestFilesLimit = 1000;
    private const int IssueDisplayLimit = 20_000;

    private readonly KnowledgeBase? _kb;
    private readonly string? _kbError;
    private CancellationTokenSource? _cts;
    private ResultsContext? _ctx;

    public MainViewModel()
    {
        IsElevated = ElevationInfo.IsProcessElevated();
        ElevationText = IsElevated
            ? "Running as administrator: Deep scan reads every corner of the drive and Windows' program-run records."
            : "Running with normal rights. Tick Deep scan to look everywhere (Windows will ask for permission).";
        DeepScan = IsElevated;

        try { _kb = KnowledgeBase.LoadDefault(); }
        catch (Exception ex) { _kbError = ex.Message; }

        RefreshDrives();
        SelectedDrive = Drives.FirstOrDefault();

        Ai = new AiController(() => _ctx);
        FolderAi = new AiExplainBlock(Ai, () => _ctx);
        TreemapAi = new AiExplainBlock(Ai, () => _ctx);
    }

    /// <summary>The optional local AI that explains items the rules couldn't identify.</summary>
    public AiController Ai { get; }
    public AiExplainBlock FolderAi { get; }
    public AiExplainBlock TreemapAi { get; }

    partial void OnGoEasyChanged(bool value) => Ai.GentleOnPc = value;

    public ObservableCollection<DriveOption> Drives { get; } = [];
    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<DetailRow> Details { get; } = [];
    public ObservableCollection<FileRow> LargestFiles { get; } = [];
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<AppRow> Programs { get; } = [];
    public ObservableCollection<CategoryRow> Categories { get; } = [];
    public ObservableCollection<ExtensionRow> Extensions { get; } = [];
    public ObservableCollection<ProtectionRow> ProtectionSummary { get; } = [];
    public ObservableCollection<UsageSourceRow> UsageSources { get; } = [];
    public ObservableCollection<CleanupRow> DeletableItems { get; } = [];
    public ObservableCollection<CleanupRow> ReviewItems { get; } = [];
    public ObservableCollection<CleanupRow> ToolItems { get; } = [];
    public ObservableCollection<UninstallRow> UninstallItems { get; } = [];
    public ObservableCollection<DuplicateRow> Duplicates { get; } = [];
    public ObservableCollection<RecentFileRow> RecentFiles { get; } = [];
    public ObservableCollection<ReferenceRow> References { get; } = [];

    public bool IsElevated { get; }
    public string ElevationText { get; }

    [ObservableProperty]
    public partial DriveOption? SelectedDrive { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    public partial string TargetPath { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand), nameof(CancelScanCommand), nameof(BrowseFolderCommand))]
    public partial bool IsScanning { get; set; }

    /// <summary>When off, only the file list is made (faster, but no "what is it" or "belongs to").</summary>
    [ObservableProperty] public partial bool LookInsideFiles { get; set; } = true;

    /// <summary>Gentle mode: fewer files at once, low disk priority, capped speed (see AnalysisOptions.Gentle).</summary>
    [ObservableProperty] public partial bool GoEasy { get; set; } = true;

    /// <summary>Read the drive directly as administrator: every corner, exact sizes, and Windows' program-run records.</summary>
    [ObservableProperty] public partial bool DeepScan { get; set; }

    /// <summary>Starts a scan requested on the command line (used when PuterScanny restarts itself as administrator).</summary>
    public async Task StartFromCommandLineAsync(Elevation.Request request)
    {
        TargetPath = request.Path;
        SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root.TrimEnd('\\'), request.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        TargetPath = request.Path;
        LookInsideFiles = request.LookInside;
        GoEasy = request.Gentle;
        DeepScan = request.Deep;
        if (StartScanCommand.CanExecute(null)) await StartScanCommand.ExecuteAsync(null);
    }

    [ObservableProperty] public partial bool HasResults { get; set; }
    [ObservableProperty] public partial string PhaseText { get; set; } = "";
    [ObservableProperty] public partial string StageText { get; set; } = "";
    [ObservableProperty] public partial string ProgressStats { get; set; } = "";
    [ObservableProperty] public partial string CurrentPath { get; set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string SummaryTitle { get; set; } = "";
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string AnalysisNote { get; set; } = "";
    [ObservableProperty] public partial string IssuesHeader { get; set; } = "Skipped & issues";
    [ObservableProperty] public partial string LargestHeader { get; set; } = "Largest files";
    [ObservableProperty] public partial string ProgramsHeader { get; set; } = "Programs";
    [ObservableProperty] public partial string ReferencesNote { get; set; } = "";
    [ObservableProperty] public partial string CleanupHeadline { get; set; } = "";
    [ObservableProperty] public partial string CleanupDetail { get; set; } = "";
    [ObservableProperty] public partial string DeletableHeader { get; set; } = "Delete";
    [ObservableProperty] public partial string ReviewHeader { get; set; } = "Review";
    [ObservableProperty] public partial string ToolHeader { get; set; } = "Windows tools";
    [ObservableProperty] public partial string UninstallHeader { get; set; } = "Programs";
    [ObservableProperty] public partial string DuplicatesHeader { get; set; } = "Duplicates";
    [ObservableProperty] public partial string StatusText { get; set; } = "Pick a drive or folder, then press Scan.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInExplorerCommand))]
    public partial TreeNodeViewModel? SelectedNode { get; set; }

    partial void OnSelectedDriveChanged(DriveOption? value)
    {
        if (value is not null) TargetPath = value.Root;
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) => BuildDetails(value?.Entry);

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? DriveTypeName(d.DriveType) : d.VolumeLabel;
                Drives.Add(new DriveOption(d.Name,
                    $"{d.Name.TrimEnd('\\')}  {label}  —  {Display.Size(d.TotalFreeSpace)} free of {Display.Size(d.TotalSize)}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive vanished or is locked (e.g. BitLocker); just leave it out of the list.
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditTarget))]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan", Multiselect = false };
        if (Directory.Exists(TargetPath)) dialog.InitialDirectory = TargetPath;
        if (dialog.ShowDialog() == true)
        {
            SelectedDrive = null;
            TargetPath = dialog.FolderName;
        }
    }

    private bool CanEditTarget() => !IsScanning;

    private bool CanStartScan() => !IsScanning && !string.IsNullOrWhiteSpace(TargetPath);

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        string target = TargetPath.Trim();
        if (!Directory.Exists(target))
        {
            MessageBox.Show($"\"{target}\" is not a folder that can be reached.", "PuterScanny", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_kb is null)
        {
            MessageBox.Show($"PuterScanny's knowledge files could not be loaded, so files cannot be identified:\n\n{_kbError}\n\nTry reinstalling the app.",
                "PuterScanny", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        bool deep = DeepScan;
        if (deep && !IsElevated)
        {
            switch (Elevation.TryRelaunchElevated(target, LookInsideFiles, GoEasy))
            {
                case Elevation.Result.Started:
                    Application.Current.Shutdown(); // the administrator copy carries on with this scan
                    return;
                case Elevation.Result.Declined:
                    if (MessageBox.Show("Deep scan needs administrator rights, and permission wasn't given.\n\nRun a standard scan instead? " +
                            "It covers everything you can open, and the report will list what couldn't be checked.",
                            "PuterScanny", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    deep = false;
                    break;
                default:
                    MessageBox.Show("PuterScanny couldn't restart as administrator. Running a standard scan instead.", "PuterScanny",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    deep = false;
                    break;
            }
        }

        // Was the last scan of this place interrupted (crash, power cut, Cancel)? Offer to continue it.
        string root = LongPath.NormalizeRoot(target);
        InterruptedScan? interrupted = await Task.Run(() => InterruptedScans.Find(root));
        if (interrupted is not null)
        {
            string checkedText = interrupted.OwnershipDone
                ? $"after checking {interrupted.FilesChecked:N0} of {interrupted.Files:N0} files ({interrupted.FilesChecked * 100.0 / Math.Max(1, interrupted.Files):0}%)"
                : "while it was matching files to programs";
            var answer = MessageBox.Show(
                $"Your last scan of {interrupted.RootPath} (started {Display.Date(interrupted.StartedUtc)}) was interrupted {checkedText}.\n\n" +
                "Continue where it left off?\n\n" +
                "Yes: keep the saved file list and only check the remaining files.\n" +
                "No: start a new scan from the beginning.",
                "Continue interrupted scan?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No)
            {
                try { InterruptedScans.Decline(interrupted.FilePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                interrupted = null;
            }
        }

        CloseResults();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        PhaseText = interrupted is null ? "Preparing…" : "Continuing the interrupted scan…";
        StageText = interrupted is null ? "Step 1 of 4" : "";
        ProgressStats = "";
        CurrentPath = "";
        StatusText = interrupted is null ? $"Scanning {target}…" : $"Continuing the scan of {interrupted.RootPath}…";

        string dbPath = interrupted?.FilePath ?? ScanDatabase.NewScanFilePath();
        var progress = new Progress<PipelineProgress>(OnProgress);
        var options = new ScanOptions { RootPath = target, Mode = deep ? ScanMode.Deep : ScanMode.Standard };
        var analysis = new AnalysisOptions { IdentifyContents = LookInsideFiles, VerifySignatures = LookInsideFiles, Gentle = GoEasy };
        var pipeline = new ScanPipeline(_kb, PathTokens.ForThisMachine(), analysis);
        CancellationToken token = _cts.Token;
        var clock = Stopwatch.StartNew();

        try
        {
            ScanSummary summary = interrupted is null
                ? await Task.Run(() => pipeline.Run(options, dbPath, progress, token))
                : await Task.Run(() => pipeline.Resume(dbPath, progress, token));
            IsScanning = false;
            LoadResults(dbPath);
            StatusText = summary.Status switch
            {
                ScanStatus.Completed when token.IsCancellationRequested =>
                    "Paused. The file list is complete; to finish checking the rest, scan the same place again and choose \"continue\".",
                ScanStatus.Completed => $"Finished in {Display.Elapsed(clock.Elapsed)}. Results saved to {dbPath}",
                ScanStatus.Cancelled => "Scan cancelled. Showing what was found before stopping (totals are incomplete).",
                ScanStatus.Failed => $"Scan stopped because of an error: {summary.ErrorMessage}. Showing partial results.",
                _ => "",
            };
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            MessageBox.Show($"The scan could not run:\n\n{ex.Message}", "PuterScanny", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelScan()
    {
        _cts?.Cancel();
        PhaseText = "Stopping… (progress so far is saved)";
    }

    [RelayCommand]
    private static void OpenScansFolder()
    {
        Directory.CreateDirectory(ScanDatabase.DefaultScansFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ScanDatabase.DefaultScansFolder}\"") { UseShellExecute = true });
    }

    private bool CanOpenInExplorer() => SelectedNode?.Entry is not null;

    [RelayCommand(CanExecute = nameof(CanOpenInExplorer))]
    private void OpenInExplorer()
    {
        if (SelectedNode?.Entry is { } e) RevealInExplorer(e.Path);
    }

    [RelayCommand]
    private static void RevealFile(string? path)
    {
        if (!string.IsNullOrEmpty(path)) RevealInExplorer(path);
    }

    public void CancelIfScanning() => _cts?.Cancel();

    // ---------------------------------------------------------------- progress & results

    private void OnProgress(PipelineProgress p)
    {
        if (!IsScanning) return;
        PhaseText = p.Headline;
        StageText = p.Phase switch
        {
            ScanPhase.Preparing or ScanPhase.Enumerating or ScanPhase.ComputingTotals => "Step 1 of 4 · listing",
            ScanPhase.CollectingInventory => "Step 2 of 4 · programs",
            ScanPhase.MatchingOwners => "Step 3 of 4 · ownership",
            ScanPhase.IdentifyingFiles => GoEasy ? "Step 4 of 4 · identifying (going easy on this PC)" : "Step 4 of 4 · identifying",
            _ => StageText,
        };
        ProgressStats = p.Scan is { } s
            ? $"{p.Detail}  ·  {Display.Elapsed(s.Elapsed)}"
            : p.Detail ?? "";
        CurrentPath = p.CurrentPath ?? "";
        if (p.Fraction is double f)
        {
            IsProgressIndeterminate = false;
            ProgressValue = f * 100;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    private void LoadResults(string dbPath)
    {
        var db = ScanDatabase.Open(dbPath);
        _ctx = new ResultsContext(db, _kb!);
        ScanSummary? summary = db.GetSummary();
        FileSystemEntry? root = db.GetRoot();
        if (summary is null || root is null) return;

        var apps = db.GetApps();
        string analysisStatus = db.GetMeta("analysis.status") ?? "not run";

        SummaryTitle = summary.RootPath + (summary.Mode == ScanMode.Deep ? "  (Deep scan)" : "");
        string onDisk = summary.TotalSizeOnDisk != summary.TotalSize ? $" ({Display.Size(summary.TotalSizeOnDisk)} on disk)" : "";
        string streams = summary.StreamCount > 0
            ? $"  ·  {Display.Count(summary.StreamCount, "hidden data stream", "hidden data streams")} ({Display.Size(summary.StreamBytes)})"
            : "";
        string status = summary.Status == ScanStatus.Completed ? "" : $"  ·  {summary.Status.ToString().ToUpperInvariant()}: partial results";
        SummaryText =
            $"{Display.Size(summary.TotalSize)}{onDisk}  ·  {Display.Count(summary.FileCount, "file", "files")}  ·  " +
            $"{Display.Count(summary.DirectoryCount, "folder", "folders")}{streams}  ·  {Display.Count(summary.IssueCount, "item skipped/noted", "items skipped/noted")}  ·  " +
            $"{Display.Count(apps.Count, "program found", "programs found")} on this PC{(summary.IsElevated ? "  ·  administrator" : "")}{status}";
        if (root.Analysis is { ReclaimBytes: > 0 } ra)
            SummaryText += $"\nCould be freed: {Display.Size(ra.ReclaimBytes)}  ·  worth reviewing: {Display.Size(ra.ReviewBytes)}  ·  with Windows tools: {Display.Size(ra.ToolBytes)}  (see the Clean up tab)";

        var problems = db.GetInventoryProblems();
        AnalysisNote = analysisStatus switch
        {
            "complete" when db.GetMeta("analysis.contents") == "0" => "Files were not opened (\"Look inside files\" was off), so file types come from names only.",
            "complete" => problems.Count > 0 ? "Some program lists could not be read: " + string.Join("; ", problems) : "",
            "cancelled" => "Checking was paused: some files show only what their name suggests. Scan the same place again to continue.",
            "running" => "Checking did not finish: some files show only what their name suggests. Scan the same place again to continue.",
            _ when analysisStatus.StartsWith("failed", StringComparison.Ordinal) => "Analysis stopped with an error: " + analysisStatus[7..].Trim(),
            _ => "",
        };

        if (summary.Method is { } method) AnalysisNote = method + (AnalysisNote.Length > 0 ? " " + AnalysisNote : "");
        if (analysisStatus == "complete" && db.GetMeta("analysis.resumed") is { } resumedCount)
            AnalysisNote = (AnalysisNote.Length > 0 ? AnalysisNote + " " : "") +
                $"This scan was continued after an interruption{(resumedCount == "1" ? "" : $" ({resumedCount} times)")}; files changed in between may show their earlier state.";

        var rootNode = new TreeNodeViewModel(_ctx, root, root.TotalSize) { IsExpanded = true, IsSelected = true };
        RootNodes.Add(rootNode);
        SelectedNode = rootNode;

        foreach (var f in db.GetLargestFiles(LargestFilesLimit))
        {
            LargestFiles.Add(new FileRow(f.Name, Path.GetDirectoryName(f.Path) ?? "", f.Size, Display.Size(f.Size),
                f.ModifiedUtc.ToLocalTime(), Display.Date(f.ModifiedUtc), f.Path,
                f.Analysis?.TypeLabel ?? "", BelongsTo(f),
                f.Analysis is { Protection: > Protection.None } pa ? Display.ProtectionTitle(pa.Protection) : ""));
        }
        LargestHeader = $"Largest files ({LargestFiles.Count:N0})";

        foreach (var i in db.GetIssues(IssueDisplayLimit))
            Issues.Add(new IssueRow(Display.Issue(i.Kind), i.Path, i.Message));
        IssuesHeader = summary.IssueCount > Issues.Count
            ? $"Skipped & issues ({Issues.Count:N0} of {summary.IssueCount:N0} shown)"
            : $"Skipped & issues ({summary.IssueCount:N0})";

        foreach (var a in apps)
        {
            Programs.Add(new AppRow(
                a.Name, a.Publisher ?? "", a.Version ?? "", AppKind(a),
                a.InstallDate is { } d ? d.ToLocalTime().ToString("d") : "", a.InstallDate,
                a.MeasuredBytes, a.MeasuredFiles > 0 ? Display.Size(a.MeasuredBytes) : "",
                a.MeasuredFiles, a.InstallLocation ?? "", Display.SourceName(a.Source),
                a.LastUsedUtc is { } lu ? Display.DateAgo(lu) : "", a.LastUsedUtc,
                a.UseCount > 0 ? a.UseCount.ToString("N0") : "", a.UseCount,
                a.ActiveTime > TimeSpan.Zero ? Display.Duration(a.ActiveTime) : "", a.ActiveTime.TotalHours,
                a.UninstallConfidence > 0 ? $"Consider uninstalling ({a.UninstallConfidence}%)" : ""));
        }
        long withFiles = apps.Count(a => a.MeasuredFiles > 0);
        ProgramsHeader = $"Programs ({apps.Count:N0})";

        long totalBytes = Math.Max(1, summary.TotalSize);
        foreach (var c in db.GetCategoryStats())
        {
            var cat = _kb!.GetCategory(c.Category);
            Categories.Add(new CategoryRow(cat?.Name ?? c.Category, cat?.Description ?? "", c.Files, c.Bytes, Display.Size(c.Bytes), c.Bytes * 100.0 / totalBytes));
        }
        foreach (var x in db.GetExtensionStats(2000))
        {
            Extensions.Add(new ExtensionRow(x.Extension.Length == 0 ? "(none)" : x.Extension, x.Label ?? "", _kb!.CategoryName(x.Category),
                x.Files, x.Bytes, Display.Size(x.Bytes), x.OpensWith ?? ""));
        }

        foreach (var (level, files, bytes) in db.GetProtectionStats())
        {
            ProtectionSummary.Add(new ProtectionRow(Display.ProtectionTitle(level), files, Display.Size(bytes), bytes, level switch
            {
                Protection.Required => "Windows or an active service, task or registration needs these. Never deleted by PuterScanny.",
                Protection.InUse => "Running programs were using these during the scan.",
                Protection.UninstallOnly => "Files of installed programs: remove them by uninstalling the program.",
                Protection.Caution => "Deleting these has side effects (cloud sync, browser profiles, game saves, PATH…).",
                _ => "Nothing was found to depend on these. Whether they can go is in each item's recommendation.",
            }));
        }
        LoadCleanup(db);

        foreach (var s in db.GetUsageSources())
            UsageSources.Add(new UsageSourceRow(Display.UsageSourceName(s.Source), s.Available ? "Checked" : "Not checked", s.Items, s.Note));
        foreach (var f in db.GetRecentlyUsedFiles(1000))
        {
            var an = f.Analysis!;
            RecentFiles.Add(new RecentFileRow(f.Name, Display.DateAgo(an.LastUsedUtc!.Value), an.LastUsedUtc.Value,
                an.UseCount > 0 ? an.UseCount.ToString("N0") : "", an.TypeLabel ?? "", Path.GetDirectoryName(f.Path) ?? "", f.Path));
        }

        const int referenceLimit = 50_000;
        foreach (var (path, r) in db.GetAllReferences(referenceLimit))
            References.Add(new ReferenceRow(Display.ReferenceKindName(r.Kind), Display.ProtectionTitle(r.Level), path, r.Detail));
        ReferencesNote = References.Count >= referenceLimit
            ? $"Showing the first {referenceLimit:N0} dependencies (strongest first)."
            : $"{References.Count:N0} dependencies found in the scanned area.";

        LoadDashboard(db, summary, root);
        InitTreemap(root);
        HasResults = true;
        Ai.Refresh();
    }

    private void LoadCleanup(ScanDatabase db)
    {
        long recommend = 0, safe = 0, review = 0, tool = 0, recommendFiles = 0, safeFiles = 0;
        foreach (var (verdict, action, files, bytes) in db.GetVerdictStats())
        {
            if (action == CleanupAction.UseWindowsTool) { tool += bytes; continue; }
            switch (verdict)
            {
                case Verdict.RecommendDelete: recommend += bytes; recommendFiles += files; break;
                case Verdict.SafeToDelete: safe += bytes; safeFiles += files; break;
                case Verdict.Review: review += bytes; break;
            }
        }
        CleanupHeadline = recommend + safe > 0
            ? $"You could free about {Display.Size(recommend + safe)} safely"
            : "Nothing here is clearly safe to remove";
        CleanupDetail = $"{Display.Size(recommend)} recommended to delete ({recommendFiles:N0} files)  ·  {Display.Size(safe)} safe to delete ({safeFiles:N0} files)  ·  " +
                        $"{Display.Size(review)} worth reviewing  ·  {Display.Size(tool)} can be freed with Windows tools. Nothing is deleted by PuterScanny.";

        CleanupRow Row(FileSystemEntry f)
        {
            var an = f.Analysis!;
            var row = new CleanupRow(f.Depth == 0 ? f.Path : f.Name, Display.VerdictTitle(an.Verdict ?? Verdict.Keep), an.Headline ?? "",
                f.TotalSize, Display.Size(f.TotalSize), an.Confidence, f.Path, f.IsDirectory ? "Folder" : an.TypeLabel ?? "File",
                f.Id, f.IsDirectory, f.IsDirectory ? an.ContainedLastModifiedUtc ?? f.ModifiedUtc : f.ModifiedUtc, f.FileCount,
                an.Verdict ?? Verdict.Keep, an.Action);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CleanupRow.IsChecked)) UpdateBasket(); };
            return row;
        }

        foreach (var f in db.GetTopItems([Verdict.RecommendDelete, Verdict.SafeToDelete], 3000)) DeletableItems.Add(Row(f));
        foreach (var f in db.GetTopItems([Verdict.Review], 3000).Where(f => f.Analysis!.Action is not (CleanupAction.UseWindowsTool or CleanupAction.Uninstall)))
            ReviewItems.Add(Row(f));
        foreach (var f in db.GetTopItems([Verdict.Review], 500, CleanupAction.UseWindowsTool)) ToolItems.Add(Row(f));
        foreach (var a in db.GetApps().Where(a => a.UninstallConfidence > 0).OrderByDescending(a => a.MeasuredBytes))
            UninstallItems.Add(new UninstallRow(a.Name, Display.Size(a.MeasuredBytes), a.MeasuredBytes, a.UninstallConfidence, a.UninstallReason ?? "",
                a.LastUsedUtc is { } lu ? Display.DateAgo(lu) : "No record", a.Publisher ?? ""));
        foreach (var g in db.GetDuplicateGroups(2000))
            Duplicates.Add(new DuplicateRow(g.Keeper, g.Copies, Display.Size(g.FileSize), g.WastedBytes, Display.Size(g.WastedBytes), string.Join("\n", g.Others)));

        DeletableHeader = $"Delete ({Display.Size(recommend + safe)})";
        ReviewHeader = $"Review ({Display.Size(review)})";
        ToolHeader = $"Windows tools ({Display.Size(tool)})";
        UninstallHeader = $"Programs to uninstall? ({UninstallItems.Count})";
        DuplicatesHeader = $"Duplicates ({Display.Size(Duplicates.Sum(d => d.Wasted))})";
    }

    private string BelongsTo(FileSystemEntry e)
    {
        if (e.Analysis?.OwnerAppName is { } app) return app;
        return _kb?.GetRule(e.Analysis?.LocationRuleId)?.Owner ?? "";
    }

    private static string AppKind(InstalledApp a) => a switch
    {
        { Source: AppSource.Steam or AppSource.EpicGames or AppSource.Gog } => "Game",
        { IsFramework: true } => "Runtime / framework",
        { IsSystemComponent: true, Source: AppSource.StoreApp } => "Part of Windows",
        { IsSystemComponent: true } => "Component",
        { Source: AppSource.StoreApp } => "Store app",
        { Source: AppSource.Chocolatey or AppSource.Scoop } => "Package",
        _ => "Program",
    };

    private void CloseResults()
    {
        HasResults = false;
        SelectedNode = null;
        RootNodes.Clear();
        LargestFiles.Clear();
        Issues.Clear();
        Details.Clear();
        Programs.Clear();
        Categories.Clear();
        Extensions.Clear();
        ProtectionSummary.Clear();
        DeletableItems.Clear();
        ReviewItems.Clear();
        ToolItems.Clear();
        UninstallItems.Clear();
        Duplicates.Clear();
        CleanupHeadline = "";
        CleanupDetail = "";
        UsageSources.Clear();
        RecentFiles.Clear();
        References.Clear();
        AnalysisNote = "";
        ClearVisuals();
        FolderAi.Show(null);
        TreemapAi.Show(null);
        _ctx?.Db.Dispose();
        _ctx = null;
        Ai.Refresh();
    }

    private void BuildDetails(FileSystemEntry? e)
    {
        Details.Clear();
        FolderAi.Show(_ctx is null ? null : e);
        if (e is null || _ctx is null) return;
        foreach (var row in DetailsBuilder.Build(e, _ctx)) Details.Add(row);
    }

    private static string DriveTypeName(DriveType t) => t switch
    {
        DriveType.Fixed => "Local disk",
        DriveType.Removable => "Removable drive",
        DriveType.Network => "Network drive",
        DriveType.CDRom => "CD/DVD drive",
        DriveType.Ram => "RAM disk",
        _ => "Drive",
    };

    private static void RevealInExplorer(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
            MessageBox.Show($"\"{path}\" no longer exists.", "PuterScanny", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Ai.Dispose();
        _ctx?.Db.Dispose();
    }
}
