using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Analysis.Reporting;

/// <param name="AiNote">The local AI's plain-English guess at what it is, when one was written (never part of the verdict).</param>
public sealed record ReportItem(string Path, string Name, bool IsFolder, long Size, string Verdict, int Confidence, string Reason, string WhatItIs, string BelongsTo,
    string? AiNote = null);

public sealed record ReportVerdict(string Verdict, long Files, long Bytes);

public sealed record ReportCategory(string Name, long Files, long Bytes);

public sealed record ReportProgram(string Name, string? Publisher, string? Version, long SizeHere, string? LastUsed, string? Suggestion);

public sealed record ReportDuplicate(string Keep, int Copies, long FileSize, long Wasted, IReadOnlyList<string> Others);

public sealed record ReportProtection(string Level, long Files, long Bytes);

public sealed record ReportNote(string Topic, string Status, string Detail);

/// <summary>Everything a report shows, gathered once from a scan so HTML and JSON stay identical.</summary>
public sealed record ReportData
{
    public required string Root { get; init; }
    public required DateTime ScannedUtc { get; init; }
    public required string Machine { get; init; }
    public required string AppVersion { get; init; }
    public bool Elevated { get; init; }
    public bool Complete { get; init; }
    public long TotalSize { get; init; }
    public long Files { get; init; }
    public long Folders { get; init; }

    public long CanFree { get; init; }
    public long Recommended { get; init; }
    public long Safe { get; init; }
    public long Review { get; init; }
    public long WithWindowsTools { get; init; }
    public long DuplicatesWasted { get; init; }

    public IReadOnlyList<ReportVerdict> Verdicts { get; init; } = [];
    public IReadOnlyList<ReportCategory> Categories { get; init; } = [];
    public IReadOnlyList<ReportItem> ToDelete { get; init; } = [];
    public IReadOnlyList<ReportItem> ToReview { get; init; } = [];
    public IReadOnlyList<ReportItem> WindowsTools { get; init; } = [];
    public IReadOnlyList<ReportProgram> UninstallSuggestions { get; init; } = [];
    public IReadOnlyList<ReportDuplicate> Duplicates { get; init; } = [];
    public IReadOnlyList<ReportItem> LargestFiles { get; init; } = [];
    public IReadOnlyList<ReportItem> TopFolders { get; init; } = [];

    /// <summary>The biggest items PuterScanny's rules couldn't identify (with the local AI's guess when there is one).</summary>
    public IReadOnlyList<ReportItem> Unidentified { get; init; } = [];
    public IReadOnlyList<ReportProtection> ProtectionLevels { get; init; } = [];
    public IReadOnlyList<ReportNote> Coverage { get; init; } = [];
    public long SkippedItems { get; init; }
    public IReadOnlyList<ReportNote> Skipped { get; init; } = [];

    /// <summary>"No reason to remove" for items kept only because nothing pointed either way.</summary>
    public static string VerdictName(Verdict v, FactFlags facts) =>
        v == Core.Models.Verdict.Keep && facts.HasFlag(FactFlags.NoEvidence) ? "No reason to remove" : VerdictName(v);

    public static string VerdictName(Verdict v) => v switch
    {
        Core.Models.Verdict.Required => "Required by Windows",
        Core.Models.Verdict.Keep => "Keep",
        Core.Models.Verdict.KeepPersonal => "Keep (your file)",
        Core.Models.Verdict.Review => "Review",
        Core.Models.Verdict.SafeToDelete => "Safe to delete",
        Core.Models.Verdict.RecommendDelete => "Recommended to delete",
        _ => v.ToString(),
    };

    public static string ProtectionName(Protection p) => p switch
    {
        Core.Models.Protection.Required => "Must be kept",
        Core.Models.Protection.InUse => "In use",
        Core.Models.Protection.UninstallOnly => "Remove only by uninstalling",
        Core.Models.Protection.Caution => "Handle with care",
        _ => "No dependency found",
    };

    /// <summary>Gathers the report from a finished scan.</summary>
    /// <param name="aiNote">Looks up the local AI's explanation for a path (null when the AI isn't used).</param>
    public static ReportData From(ScanDatabase db, KnowledgeBase kb, int listLimit = 500, Func<string, string?>? aiNote = null)
    {
        var s = db.GetSummary() ?? throw new InvalidOperationException("The scan has no summary.");
        var root = db.GetRoot()!;

        long rec = 0, safe = 0, review = 0, tool = 0;
        var verdicts = new Dictionary<Verdict, (long Files, long Bytes)>();
        foreach (var (v, action, files, bytes) in db.GetVerdictStats())
        {
            var cur = verdicts.GetValueOrDefault(v);
            verdicts[v] = (cur.Files + files, cur.Bytes + bytes);
            if (action == CleanupAction.UseWindowsTool) { tool += bytes; continue; }
            if (v == Core.Models.Verdict.RecommendDelete) rec += bytes;
            else if (v == Core.Models.Verdict.SafeToDelete) safe += bytes;
            else if (v == Core.Models.Verdict.Review) review += bytes;
        }

        ReportItem Item(FileSystemEntry e) => new(
            e.Path, e.Depth == 0 ? e.Path : e.Name, e.IsDirectory, e.TotalSize,
            e.Analysis?.Verdict is { } v ? VerdictName(v, e.Analysis.Facts) : "",
            e.Analysis?.Confidence ?? 0,
            e.Analysis?.Headline ?? "",
            e.IsDirectory ? (kb.GetRule(e.Analysis?.LocationRuleId)?.Name ?? "Folder") : e.Analysis?.TypeLabel ?? "File",
            e.Analysis?.OwnerAppName ?? kb.GetRule(e.Analysis?.LocationRuleId)?.Owner ?? "",
            aiNote?.Invoke(e.Path));

        var unidentified = db.GetUnidentifiedItems(100, 1L << 20).Select(Item).ToList();

        var dupes = db.GetDuplicateGroups(listLimit);
        var sources = db.GetUsageSources();
        var problems = db.GetInventoryProblems();
        var coverage = new List<ReportNote>();
        coverage.AddRange(sources.Select(u => new ReportNote(UsageName(u.Source), u.Available ? "Checked" : "Not checked",
            u.Available ? $"{u.Items:N0} records" + (u.Note.Length > 0 ? ". " + u.Note : "") : u.Note)));
        coverage.Add(new ReportNote("Look inside files", db.GetMeta("analysis.contents") == "0" ? "Off" : "On",
            db.GetMeta("analysis.contents") == "0" ? "File types were judged by name only, and duplicates were not searched." : "Every file was opened to identify it; duplicates were compared byte for byte."));
        coverage.Add(aiNote is null
            ? new ReportNote("Local AI explanations", "Off", "The optional offline AI wasn't used, so unidentified items have no plain-English guess.")
            : new ReportNote("Local AI explanations", "On",
                $"{unidentified.Count(u => u.AiNote is not null):N0} of the {unidentified.Count:N0} biggest unidentified items have an AI-written guess. These are labelled, and never affect a recommendation."));
        coverage.AddRange(problems.Select(p => new ReportNote("Could not read", "Problem", p)));

        return new ReportData
        {
            Root = s.RootPath,
            ScannedUtc = s.CompletedUtc ?? s.StartedUtc,
            Machine = s.MachineName ?? Environment.MachineName,
            AppVersion = s.AppVersion ?? "",
            Elevated = s.IsElevated,
            Complete = s.Status == ScanStatus.Completed && db.GetMeta("analysis.status") == "complete",
            TotalSize = s.TotalSize,
            Files = s.FileCount,
            Folders = s.DirectoryCount,
            Recommended = rec,
            Safe = safe,
            CanFree = rec + safe,
            Review = review,
            WithWindowsTools = tool,
            DuplicatesWasted = dupes.Sum(d => d.WastedBytes),
            Verdicts = SplitNoReason(verdicts.OrderBy(kv => kv.Key).Select(kv => new ReportVerdict(VerdictName(kv.Key), kv.Value.Files, kv.Value.Bytes)), db.GetNoEvidenceStats()),
            Categories = db.GetCategoryStats().Select(c => new ReportCategory(kb.CategoryName(c.Category), c.Files, c.Bytes)).ToList(),
            ToDelete = db.GetTopItems([Core.Models.Verdict.RecommendDelete, Core.Models.Verdict.SafeToDelete], listLimit).Select(Item).ToList(),
            ToReview = db.GetTopItems([Core.Models.Verdict.Review], listLimit)
                .Where(e => e.Analysis!.Action is not (CleanupAction.UseWindowsTool or CleanupAction.Uninstall)).Select(Item).ToList(),
            WindowsTools = db.GetTopItems([Core.Models.Verdict.Review], listLimit, CleanupAction.UseWindowsTool).Select(Item).ToList(),
            UninstallSuggestions = db.GetApps().Where(a => a.UninstallConfidence > 0).OrderByDescending(a => a.MeasuredBytes)
                .Select(a => new ReportProgram(a.Name, a.Publisher, a.Version, a.MeasuredBytes, a.LastUsedUtc?.ToString("yyyy-MM-dd"), a.UninstallReason)).ToList(),
            Duplicates = dupes.Select(d => new ReportDuplicate(d.Keeper, d.Copies, d.FileSize, d.WastedBytes, d.Others)).ToList(),
            LargestFiles = db.GetLargestFiles(Math.Min(listLimit, 200)).Select(Item).ToList(),
            TopFolders = db.GetChildren(root.Id, 50).Where(c => c.IsDirectory).Select(Item).ToList(),
            Unidentified = unidentified,
            ProtectionLevels = db.GetProtectionStats().Select(p => new ReportProtection(ProtectionName(p.Level), p.Files, p.Bytes)).ToList(),
            Coverage = coverage,
            SkippedItems = s.IssueCount,
            Skipped = db.GetIssues(listLimit).Select(i => new ReportNote(i.Kind.ToString(), i.Path, i.Message)).ToList(),
        };
    }

    /// <summary>Shows "Keep" and "No reason to remove" (kept only because nothing pointed either way) as separate rows.</summary>
    private static List<ReportVerdict> SplitNoReason(IEnumerable<ReportVerdict> rows, (long Files, long Bytes) noReason)
    {
        var list = new List<ReportVerdict>();
        foreach (var row in rows)
        {
            if (row.Verdict == VerdictName(Core.Models.Verdict.Keep) && noReason.Files > 0)
            {
                long files = Math.Min(row.Files, noReason.Files), bytes = Math.Min(row.Bytes, noReason.Bytes);
                if (row.Files - files > 0) list.Add(row with { Files = row.Files - files, Bytes = row.Bytes - bytes });
                list.Add(new ReportVerdict("No reason to remove", files, bytes));
            }
            else list.Add(row);
        }
        return list;
    }

    private static string UsageName(UsageSource s) => s switch
    {
        UsageSource.ProgramLaunches => "Program launches",
        UsageSource.RecentItems => "Recent items",
        UsageSource.JumpLists => "Apps' recent lists",
        UsageSource.OfficeRecent => "Office recent documents",
        UsageSource.Steam => "Steam play history",
        UsageSource.LastAccess => "Last-access dates",
        UsageSource.Prefetch => "Program run counts (Prefetch)",
        UsageSource.BackgroundActivity => "Last program runs (BAM)",
        UsageSource.Amcache => "Program inventory (Amcache)",
        _ => s.ToString(),
    };
}
