using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Scanning;
using PuterScanny.Storage;

namespace PuterScanny.Analysis;

/// <summary>The whole job behind the Scan button: list everything, then analyse it, saving all results to one database file.</summary>
public sealed class ScanPipeline
{
    private readonly KnowledgeBase _kb;
    private readonly PathTokens _tokens;
    private readonly AnalysisOptions _analysis;

    public ScanPipeline(KnowledgeBase kb, PathTokens tokens, AnalysisOptions? analysis = null)
    {
        _kb = kb;
        _tokens = tokens;
        _analysis = analysis ?? new AnalysisOptions();
    }

    /// <summary>For tests: use this usage history instead of reading the real one from this PC.</summary>
    public Func<IReadOnlyList<InstalledApp>, PuterScanny.Signals.Usage.UsageIndex>? UsageSource { get; init; }

    public ScanSummary Run(ScanOptions options, string databasePath, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        using var db = ScanDatabase.Create(databasePath);

        var scanProgress = new SyncProgress<ScanProgress>(p => progress?.Report(new PipelineProgress(
            p.Phase,
            p.Phase switch
            {
                ScanPhase.Preparing => "Preparing…",
                ScanPhase.Enumerating => "Listing every file and folder…",
                ScanPhase.ComputingTotals => "Adding up folder sizes…",
                ScanPhase.Finalizing => "Saving and indexing the file list…",
                _ => "Scanning…",
            },
            $"{p.FilesScanned:N0} files  ·  {p.DirectoriesScanned:N0} folders  ·  {ByteSize.Format(p.BytesScanned)}  ·  {p.IssueCount:N0} skipped/noted",
            p.EstimatedFraction,
            p.CurrentPath,
            p)));

        ScanSummary summary = new FileSystemScanner().Scan(options, db.CreateSink(), scanProgress, ct);

        if (summary.Status != ScanStatus.Cancelled && !ct.IsCancellationRequested)
        {
            try
            {
                new Analyzer(_kb, _tokens, _analysis) { UsageSource = UsageSource }.Run(db, summary, progress, ct);
            }
            catch (OperationCanceledException)
            {
                // Partial analysis is kept and marked as cancelled in the database.
            }
        }

        progress?.Report(new PipelineProgress(ScanPhase.Finalizing, "Saving results…", null, null, null));
        db.Finish();
        return summary;
    }

    /// <summary>
    /// Continues an interrupted scan: the saved file list is kept, finished stages are skipped and only files not
    /// yet identified are opened. The original scan's choices (look inside files, check signatures) are kept.
    /// </summary>
    public ScanSummary Resume(string databasePath, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        using var db = ScanDatabase.Open(databasePath);
        var summary = db.GetSummary() ?? throw new InvalidDataException("The saved scan has no summary and cannot be continued.");
        var options = _analysis with
        {
            IdentifyContents = db.GetMeta("analysis.contents") != "0",
            VerifySignatures = db.GetMeta("analysis.signatures") != "0",
        };

        progress?.Report(new PipelineProgress(ScanPhase.IdentifyingFiles, "Continuing the interrupted scan…", null, null, null));
        try
        {
            new Analyzer(_kb, _tokens, options) { UsageSource = UsageSource }.Run(db, summary, progress, ct);
        }
        catch (OperationCanceledException)
        {
            // Stays resumable.
        }

        progress?.Report(new PipelineProgress(ScanPhase.Finalizing, "Saving results…", null, null, null));
        db.Finish();
        return summary;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
