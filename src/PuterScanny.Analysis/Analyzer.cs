using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Signals.Content;
using PuterScanny.Signals.Inventory;
using PuterScanny.Signals.Shell;
using PuterScanny.Signals.SystemRefs;
using PuterScanny.Signals.Usage;
using PuterScanny.Storage;

namespace PuterScanny.Analysis;

/// <summary>
/// Runs after the file listing: works out which program every file belongs to, which known Windows/app location it
/// is in, and what each file really is (by reading its content). Results go into the scan database.
/// </summary>
public sealed class Analyzer
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(200);

    private readonly KnowledgeBase _kb;
    private readonly PathTokens _tokens;
    private readonly AnalysisOptions _options;
    private readonly Dictionary<string, ExtInfo> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public Analyzer(KnowledgeBase kb, PathTokens tokens, AnalysisOptions? options = null)
    {
        _kb = kb;
        _tokens = tokens;
        _options = options ?? new AnalysisOptions();
    }

    /// <summary>For tests: use this usage history instead of reading the real one from this PC.</summary>
    public Func<IReadOnlyList<InstalledApp>, UsageIndex>? UsageSource { get; init; }

    private sealed class ExtInfo
    {
        public required string Label;
        public required string Category;
        public bool Known;
        public string? WindowsName;
        public string? OpensWith;
        public long Files;
        public long Bytes;
    }

    private sealed record DirState(PathMatcher<LocationRule>.MatchState State, long? Owner, string? RuleId, bool OwnerInstall);

    private static Protection Max(Protection a, Protection b) => a >= b ? a : b;

    private static bool StartsWithAny(string path, string[] prefixes)
    {
        foreach (string p in prefixes)
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private readonly record struct WorkItem(long Id, string Path, long Size, EntryFlags Flags, string? Ext);

    /// <summary>
    /// Runs every analysis stage. Stages already finished in an earlier, interrupted run are skipped, and file
    /// identification only processes files not yet identified, so calling this again resumes the work.
    /// </summary>
    public void Run(ScanDatabase db, ScanSummary summary, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var writer = db.CreateAnalysisWriter();
        bool ownersDone = db.GetMeta("analysis.owners") == "done";
        if (!ownersDone)
        {
            writer.ResetAnalysis(); // half-finished ownership results are cheap to redo
            writer.SetMeta("analysis.contents", _options.IdentifyContents ? "1" : "0");
            writer.SetMeta("analysis.signatures", _options.VerifySignatures ? "1" : "0");
        }
        else
        {
            int resumed = int.TryParse(db.GetMeta("analysis.resumed"), out var n) ? n : 0;
            writer.SetMeta("analysis.resumed", (resumed + 1).ToString(CultureInfo.InvariantCulture));
        }
        writer.SetMeta("analysis.status", "running");
        writer.Flush();

        try
        {
            if (!ownersDone)
            {
                // 1. What is installed on this PC?
                progress?.Report(new PipelineProgress(ScanPhase.CollectingInventory, "Finding installed programs and games…", null, null, null));
                var inventory = AppInventory.Collect(_tokens, _options.IncludeMsiComponents,
                    new SyncProgress<string>(s => progress?.Report(new PipelineProgress(ScanPhase.CollectingInventory, "Finding installed programs and games…", s, null, null))), ct);
                // Programs that run from their own folder without being installed (Windows has no record of them).
                progress?.Report(new PipelineProgress(ScanPhase.CollectingInventory, "Finding portable programs…", null, null, null));
                var portable = PortablePrograms.Find(db, _kb, _tokens, inventory.Apps, _options.PortableAppAreaOverride);
                if (portable.Count > 0) inventory = inventory.WithExtraApps(portable);
                writer.WriteApps(inventory.Apps);
                writer.WriteInventoryProblems(inventory.Problems);
                writer.Flush();

                // 2. What do Windows and installed programs depend on?
                string windir = _tokens.Values.TryGetValue("windir", out var w) ? w : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var references = SystemReferenceCollector.Collect(windir, inventory.Apps,
                    new SyncProgress<string>(s => progress?.Report(new PipelineProgress(ScanPhase.CollectingInventory, "Finding what Windows and programs depend on…", s, null, null))), ct);
                writer.WriteInventoryProblems(references.Problems);

                // 3. When were things last used, and how often?
                var usage = UsageSource?.Invoke(inventory.Apps) ?? UsageCollector.Collect(inventory.Apps,
                    new SyncProgress<string>(s => progress?.Report(new PipelineProgress(ScanPhase.CollectingInventory, "Reading usage history…", s, null, null))), ct);
                writer.WriteUsageSources(usage.Sources);
                writer.SetMeta("usage.last_access_reliable", usage.LastAccessReliable ? "1" : "0");

                // 4. Who owns each item, which known location is it in, what must be kept, and when was it used?
                MatchOwners(db, writer, inventory, references, usage, summary, progress, ct);
                writer.SetMeta("analysis.owners", "done");
                writer.Flush();
            }

            // 5. What is each file, really?
            if (_options.IdentifyContents)
            {
                IdentifyFiles(db, writer, summary, progress, ct);
                writer.Flush();
            }

            // 6. Folders: the strongest protection and the latest use of anything inside.
            RollUpProtection(db, writer, progress, ct);
            writer.Flush();

            // 7. Duplicates, leftovers and the keep-or-delete verdict for everything.
            new RecommendPass(_kb, _tokens, _options).Run(db, writer, progress, ct);

            writer.SetMeta("analysis.status", "complete");
        }
        catch (OperationCanceledException)
        {
            writer.SetMeta("analysis.status", "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            writer.SetMeta("analysis.status", "failed: " + ex.Message);
            throw;
        }
        finally
        {
            double previous = double.TryParse(db.GetMeta("analysis.seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
            writer.SetMeta("analysis.seconds", (previous + clock.Elapsed.TotalSeconds).ToString("0", CultureInfo.InvariantCulture));
        }
    }

    // ------------------------------------------------------------------ ownership & locations

    private void MatchOwners(ScanDatabase db, AnalysisWriter writer, AppInventory inventory, SystemReferences references, UsageIndex usage,
        ScanSummary summary, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        // Usage of a program = usage of its own files (its .exe launches) plus program-level records (Steam, Store apps).
        var appEvents = new Dictionary<long, List<UsageEvent>>();
        foreach (var (appId, events) in usage.ByApp)
        {
            appEvents[appId] = [.. events];
            foreach (var e in events) writer.AddAppUsage(appId, e);
        }
        var generic = new GenericFolders(_tokens);
        var rootOwner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Program folders and registered files (as opposed to data folders such as a Store app's settings or Steam's shader cache):
        // their contents are removed by uninstalling, never by deleting files.
        var installRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wrpRoots = new[] { "windir", "programfiles", "programfilesx86" }
            .Select(t => _tokens.Values.TryGetValue(t, out var v) ? v.TrimEnd('\\') + "\\" : null)
            .OfType<string>().ToArray();
        var exactOwner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var storeFamilies = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // When several entries claim the same folder (a program and its plugins/updates), the real program wins:
        // visible before hidden components, programs before runtimes, then the biggest install.
        var claimants = inventory.Apps
            .OrderBy(a => a.IsSystemComponent)
            .ThenBy(a => a.IsFramework)
            .ThenByDescending(a => a.EstimatedSizeBytes ?? 0)
            .ThenBy(a => a.Id);
        foreach (var app in claimants)
        {
            if (app.InstallLocation is { } loc && !generic.IsGeneric(loc))
            {
                rootOwner.TryAdd(loc.TrimEnd('\\'), app.Id);
                installRoots.Add(loc.TrimEnd('\\'));
            }
            foreach (var extra in app.ExtraFolders)
                if (!generic.IsGeneric(extra)) rootOwner.TryAdd(extra.TrimEnd('\\'), app.Id);
            foreach (var file in app.OwnedFiles)
                if (!generic.IsGeneric(file))
                {
                    exactOwner.TryAdd(file, app.Id);
                    installRoots.Add(file);
                }
            if (app.Source == AppSource.StoreApp && app.SourceKey is { } family) storeFamilies.TryAdd(family, app.Id);
        }

        var matcher = _kb.BuildLocationMatcher(_tokens);
        var dirs = new Dictionary<long, DirState>();
        var appTotals = new Dictionary<long, (long Bytes, long Files)>();
        long total = summary.FileCount + summary.DirectoryCount + 1;
        long done = 0;
        var clock = Stopwatch.StartNew();
        TimeSpan last = TimeSpan.Zero;

        using var read = db.OpenReadConnection();
        using var cmd = read.CreateCommand();
        cmd.CommandText = "SELECT id, parent_id, name, path, is_dir, size FROM entry ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long id = r.GetInt64(0);
            long? parentId = r.IsDBNull(1) ? null : r.GetInt64(1);
            string name = r.GetString(2);
            string path = r.GetString(3);
            bool isDir = r.GetInt32(4) != 0;
            long size = r.GetInt64(5);

            PathMatcher<LocationRule>.MatchState state;
            string? ruleId;
            long? owner;
            bool exact = false;
            bool ownerInstall;

            if (parentId is null || !dirs.TryGetValue(parentId.Value, out var parent))
            {
                // The scan root: walk its full path so ancestors' rules and owners apply (e.g. scanning a folder inside Program Files).
                (state, ruleId, owner, ownerInstall) = WalkAncestors(matcher, path, rootOwner, installRoots);
                if (owner is not null && rootOwner.TryGetValue(path.TrimEnd('\\'), out _)) exact = true;
            }
            else
            {
                state = matcher.Advance(parent.State, name);
                LocationRule? hit = matcher.TryGetBest(state, out var rule) ? rule : null;
                ruleId = hit?.Id ?? parent.RuleId;

                if (exactOwner.TryGetValue(path, out long fileOwner) || isDir && rootOwner.TryGetValue(path, out fileOwner))
                {
                    owner = fileOwner;
                    exact = true;
                    ownerInstall = installRoots.Contains(path);
                }
                else if (hit?.Resolver == "appx-data" && storeFamilies.TryGetValue(name, out long storeApp))
                {
                    owner = storeApp;
                    exact = true;
                    ownerInstall = false;
                }
                else
                {
                    owner = parent.Owner;
                    ownerInstall = parent.OwnerInstall;
                }
            }

            // What must be kept: the location's own protection, the owning program, and anything that depends on it.
            Protection protection = _kb.GetRule(ruleId)?.Protection ?? Protection.None;
            if (owner is not null && ownerInstall) protection = Max(protection, Protection.UninstallOnly);
            if (references.TryGet(path.TrimEnd('\\'), out var refs))
            {
                foreach (var reference in refs)
                {
                    protection = Max(protection, reference.Level);
                    writer.AddReference(id, reference);
                }
            }
            if (!isDir && StartsWithAny(path, wrpRoots) && WindowsResourceProtection.IsProtected(path))
            {
                protection = Protection.Required;
                writer.AddReference(id, new SystemReference(ReferenceKind.WindowsProtected, Protection.Required,
                    "Protected by Windows Resource Protection: Windows itself guards this file and restores it if it is changed or removed"));
            }

            string? category = null, label = null;
            if (isDir)
            {
                dirs[id] = new DirState(state, owner, ruleId, ownerInstall);
            }
            else
            {
                string ext = Path.GetExtension(name).ToLowerInvariant();
                var info = GetExtInfo(ext);
                info.Files++;
                info.Bytes += size;
                category = info.Category;
                label = info.Label;
                if (owner is long o)
                {
                    appTotals.TryGetValue(o, out var t);
                    appTotals[o] = (t.Bytes + size, t.Files + 1);
                }
            }

            UsageSummary? used = null;
            if (usage.TryGet(path.TrimEnd('\\'), out var events))
            {
                foreach (var e in events) writer.AddUsage(id, e);
                used = UsageIndex.Summarize(events);
                if (owner is long app && ownerInstall)
                {
                    if (!appEvents.TryGetValue(app, out var list)) appEvents[app] = list = [];
                    list.AddRange(events);
                }
            }

            writer.AddAnalysis(id, owner, exact, ruleId, category, label, protection, used);

            if ((++done & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed - last > ReportEvery)
                {
                    last = clock.Elapsed;
                    progress?.Report(new PipelineProgress(ScanPhase.MatchingOwners, "Matching files to programs and known locations…",
                        $"{done:N0} of {total:N0} items", Math.Min(1, (double)done / total), path));
                }
            }
        }

        writer.UpdateAppMeasurements(appTotals.Select(kv => (kv.Key, kv.Value.Bytes, kv.Value.Files)));
        writer.SetAppUsage(appEvents.Select(kv => (kv.Key, UsageIndex.Summarize(kv.Value))));
        writer.WriteExtensionStats(_extensions.Select(kv => new ExtensionStat(
            kv.Key, kv.Value.Files, kv.Value.Bytes, kv.Value.Label, kv.Value.Category, kv.Value.Known, kv.Value.WindowsName, kv.Value.OpensWith)));
    }

    private static (PathMatcher<LocationRule>.MatchState, string?, long?, bool) WalkAncestors(
        PathMatcher<LocationRule> matcher, string rootPath, Dictionary<string, long> rootOwner, HashSet<string> installRoots)
    {
        var state = matcher.Start;
        string? ruleId = null;
        long? owner = null;
        bool install = false;
        string prefix = "";
        foreach (string seg in rootPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            prefix = prefix.Length == 0 ? seg : prefix + "\\" + seg;
            state = matcher.Advance(state, seg);
            if (matcher.TryGetBest(state, out var rule)) ruleId = rule.Id;
            if (rootOwner.TryGetValue(prefix, out long o))
            {
                owner = o;
                install = installRoots.Contains(prefix);
            }
        }
        return (state, ruleId, owner, install);
    }

    // ------------------------------------------------------------------ folder roll-up

    /// <summary>
    /// For every folder, the strongest protection of anything inside it, so a folder that holds even one file Windows
    /// needs is never treated as safe to delete as a whole.
    /// </summary>
    private static void RollUpProtection(ScanDatabase db, AnalysisWriter writer, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new PipelineProgress(ScanPhase.Finalizing, "Adding up what each folder holds and when it was last used…", null, null, null));
        var parentOf = new Dictionary<long, long?>();
        var own = new Dictionary<long, Protection>();
        var contained = new Dictionary<long, Protection>();
        var ownUsed = new Dictionary<long, long>();
        var containedUsed = new Dictionary<long, long>();
        var containedModified = new Dictionary<long, long>();

        using (var read = db.OpenReadConnection())
        using (var cmd = read.CreateCommand())
        {
            cmd.CommandText = "SELECT e.id, e.parent_id, e.is_dir, COALESCE(a.protection, 0), a.last_used, e.modified_utc FROM entry e LEFT JOIN analysis a ON a.entry_id = e.id";
            using var r = cmd.ExecuteReader();
            long n = 0;
            while (r.Read())
            {
                if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                long id = r.GetInt64(0);
                long? parent = r.IsDBNull(1) ? null : r.GetInt64(1);
                var level = (Protection)r.GetInt32(3);
                long used = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                if (r.GetInt32(2) != 0)
                {
                    parentOf[id] = parent;
                    own[id] = level;
                    if (used > 0) ownUsed[id] = used;
                }
                else if (parent is long p)
                {
                    if (level > Protection.None) contained[p] = Max(contained.GetValueOrDefault(p), level);
                    if (used > containedUsed.GetValueOrDefault(p)) containedUsed[p] = used;
                    long modified = r.GetInt64(5);
                    if (modified > containedModified.GetValueOrDefault(p)) containedModified[p] = modified;
                }
            }
        }

        // Children always have higher ids than their parents: fold from the deepest up.
        foreach (long id in parentOf.Keys.OrderByDescending(k => k))
        {
            if (parentOf[id] is not long parent) continue;
            var carried = Max(own[id], contained.GetValueOrDefault(id));
            if (carried > Protection.None) contained[parent] = Max(contained.GetValueOrDefault(parent), carried);
            long lastInside = Math.Max(ownUsed.GetValueOrDefault(id), containedUsed.GetValueOrDefault(id));
            if (lastInside > containedUsed.GetValueOrDefault(parent)) containedUsed[parent] = lastInside;
            long changedInside = containedModified.GetValueOrDefault(id);
            if (changedInside > containedModified.GetValueOrDefault(parent)) containedModified[parent] = changedInside;
        }

        writer.SetContainedProtection(contained.Where(kv => kv.Value > Protection.None).Select(kv => (kv.Key, kv.Value)));
        writer.SetContainedLastUsed(containedUsed.Select(kv => (kv.Key, new DateTime(kv.Value, DateTimeKind.Utc))));
        writer.SetContainedLastModified(containedModified.Where(kv => kv.Value > 0).Select(kv => (kv.Key, new DateTime(kv.Value, DateTimeKind.Utc))));
    }

    private ExtInfo GetExtInfo(string ext)
    {
        if (_extensions.TryGetValue(ext, out var info)) return info;

        var kb = _kb.GetType(ext.Length == 0 ? null : ext);
        AssociationInfo? assoc = ext.Length > 1 ? FileAssociations.Lookup(ext) : null;
        string label = kb?.Description
                       ?? assoc?.FriendlyTypeName
                       ?? (ext.Length == 0 ? "File (no extension)" : $"{ext.TrimStart('.').ToUpperInvariant()} file");
        info = new ExtInfo
        {
            Label = label,
            Category = kb?.Category ?? "other",
            Known = kb is not null,
            WindowsName = assoc?.FriendlyTypeName,
            OpensWith = assoc?.OpensWith,
        };
        _extensions[ext] = info;
        return info;
    }

    // ------------------------------------------------------------------ content identification

    private void IdentifyFiles(ScanDatabase db, AnalysisWriter writer, ScanSummary summary, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        int workers = _options.Workers > 0 ? _options.Workers
            : _options.Gentle ? 2
            : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        var limiter = new RateLimiter(_options.Gentle ? _options.GentleFilesPerSecond : 0);
        long total = Math.Max(1, summary.FileCount);
        long alreadyDone = CountIdentified(db);

        using var identifier = new FileIdentifier(_kb, _options.VerifySignatures, oneLargeFileAtATime: _options.Gentle);
        using var input = new BlockingCollection<WorkItem>(boundedCapacity: 5_000);
        using var output = new BlockingCollection<(ContentIdentity Identity, string? Ext)>(boundedCapacity: 5_000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        var producer = Task.Run(() =>
        {
            try
            {
                using var read = db.OpenReadConnection();
                using var cmd = read.CreateCommand();
                // Only files not identified yet, so an interrupted run picks up where it stopped.
                cmd.CommandText = """
                    SELECT e.id, e.path, e.size, e.flags, e.name
                    FROM entry e JOIN analysis a ON a.entry_id = e.id
                    WHERE e.is_dir = 0 AND a.read_status = 0
                    ORDER BY e.id
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string name = r.GetString(4);
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    input.Add(new WorkItem(r.GetInt64(0), r.GetString(1), r.GetInt64(2), (EntryFlags)r.GetInt64(3), ext.Length == 0 ? null : ext), token);
                }
            }
            finally
            {
                input.CompleteAdding();
            }
        }, token);

        var consumers = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(() =>
        {
            bool background = _options.Gentle && BackgroundThread.Enter();
            try
            {
                foreach (var item in input.GetConsumingEnumerable(token))
                {
                    limiter.Wait(token);
                    ContentIdentity result;
                    try
                    {
                        result = identifier.Identify(item.Id, item.Path, item.Size, item.Ext, item.Flags);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = new ContentIdentity { EntryId = item.Id, ReadStatus = ReadStatus.Error };
                    }
                    output.Add((result, item.Ext), token);
                }
            }
            finally
            {
                if (background) BackgroundThread.Leave();
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var allDone = Task.WhenAll(consumers).ContinueWith(_ => output.CompleteAdding(), TaskScheduler.Default);

        long doneThisRun = 0, programs = 0, unreadable = 0, mismatches = 0;
        var clock = Stopwatch.StartNew();
        TimeSpan last = TimeSpan.Zero;
        string headline = alreadyDone > 0 ? "Continuing: looking inside the remaining files…" : "Looking inside every file…";
        try
        {
            foreach (var (id, ext) in output.GetConsumingEnumerable(token))
            {
                if (id.Pe is not null) programs++;
                if (id.ReadStatus is ReadStatus.AccessDenied or ReadStatus.InUse or ReadStatus.Error) unreadable++;
                if (id.ExtensionMismatch) mismatches++;

                var (category, label) = Describe(id, ext);
                writer.UpdateIdentity(id, category, label);
                doneThisRun++;

                if (clock.Elapsed - last > ReportEvery)
                {
                    last = clock.Elapsed;
                    writer.FlushIfOlderThan(TimeSpan.FromSeconds(5)); // a crash costs seconds, not the whole scan
                    long done = alreadyDone + doneThisRun;
                    progress?.Report(new PipelineProgress(ScanPhase.IdentifyingFiles, headline,
                        $"{done:N0} of {total:N0} files{TimeLeft(doneThisRun, total - done, clock.Elapsed)}  ·  {programs:N0} program files checked  ·  " +
                        $"{mismatches:N0} with a misleading name  ·  {unreadable:N0} locked or protected",
                        Math.Min(1, (double)done / total), null));
                }
            }
            Task.WaitAll([producer, .. consumers, allDone]);
        }
        catch (Exception)
        {
            linked.Cancel();
            try { Task.WaitAll([producer, .. consumers]); } catch (AggregateException) { }
            throw;
        }

        ct.ThrowIfCancellationRequested();
    }

    private static long CountIdentified(ScanDatabase db)
    {
        using var read = db.OpenReadConnection();
        using var cmd = read.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM analysis a JOIN entry e ON e.id = a.entry_id WHERE e.is_dir = 0 AND a.read_status <> 0";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string TimeLeft(long done, long remaining, TimeSpan elapsed)
    {
        if (done < 2_000 || elapsed.TotalSeconds < 15 || remaining <= 0) return "";
        double seconds = remaining / (done / elapsed.TotalSeconds);
        return seconds switch
        {
            < 90 => "  ·  about a minute left",
            < 3600 => $"  ·  about {Math.Round(seconds / 60):0} min left",
            _ => $"  ·  about {seconds / 3600:0.#} hours left",
        };
    }

    /// <summary>Picks the best plain-English label: the curated name when it agrees with the content, the real format when not.</summary>
    private (string? Category, string? Label) Describe(ContentIdentity id, string? ext)
    {
        var info = GetExtInfo(ext ?? "");
        string category = id.Category ?? info.Category;

        if (id.FormatId is null) return (category, info.Label);                       // not opened: trust the name
        if (id.FormatId.StartsWith("pe-", StringComparison.Ordinal) || id.FormatId == "dos")
            return (category, id.FormatName);                                          // program files: say exactly what kind
        if (id.ExtensionMismatch)
            return (id.Category ?? category, $"{id.FormatName} (named \"{ext}\")");
        if (id.FormatId == "empty") return (category, $"{info.Label} (empty)");
        if (info.Known)
        {
            // A vague extension (".dat", ".tmp", ".bak"…) plus a recognised format: say both.
            bool vagueExtension = info.Category is "data" or "other" or "temporary" or "backup" or "cache" or "system";
            bool specificFormat = id.FormatId is not ("binary" or "text" or "zero-filled");
            if (vagueExtension && specificFormat)
                return (info.Category is "data" or "other" ? id.Category ?? category : info.Category, $"{info.Label}: {id.FormatName}");
            return (info.Category, info.Label);
        }
        if (id.FormatId is "binary" or "text") return (category, info.WindowsName ?? (ext is null ? id.FormatName : info.Label));
        return (category, id.FormatName);
    }

    /// <summary>Forwards progress synchronously on the calling thread (Progress&lt;T&gt; would post to the UI context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
