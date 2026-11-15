using System.Diagnostics;
using System.Text;
using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Signals.Usage;
using PuterScanny.Storage;

namespace PuterScanny.Analysis;

/// <summary>
/// The last analysis stage: finds duplicates, leftovers, empty folders, broken shortcuts and old installers,
/// asks <see cref="Recommender"/> for a verdict on every file and folder, adds up how much each folder could free,
/// and suggests programs to uninstall.
/// </summary>
public sealed class RecommendPass(KnowledgeBase kb, PathTokens tokens, AnalysisOptions options)
{
    private static readonly string[] PartialDownloadExtensions = [".crdownload", ".part", ".partial", ".download", ".opdownload"];

    /// <summary>Folder names that belong to Windows or hardware drivers, never "leftovers" of an uninstalled program.</summary>
    private static readonly HashSet<string> NeverLeftover = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Programs", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "Publishers",
        "PeerDistRepub", "VirtualStore", "Windows", "Package Cache", "Microsoft Help", "IsolatedStorage", "History",
        "Application Data", "Downloaded Installations", "SquirrelTemp", "USOShared", "USOPrivate", "SoftwareDistribution",
        "regid.1991-06.com.microsoft", "WindowsHolographicDevices", "Intel", "Intel Corporation", "NVIDIA", "NVIDIA Corporation",
        "AMD", "ATI", "Realtek", "Dell", "HP", "Hewlett-Packard", "Lenovo", "ASUS", "Alienware", "Common Files", "Windows Defender",
        "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Sidebar",
        "WindowsPowerShell", "Internet Explorer", "Microsoft.NET", "Reference Assemblies", "MSBuild", "Uninstall Information",
        "ModifiableWindowsApps", "WindowsApps", "Desktop", "Documents", "Start Menu", "Templates", "ssh", ".ssh", "Microsoft OneDrive",
        "OneDrive", "Mozilla", "Google", "Adobe", "Apple", "Apple Computer", "Sun", "Oracle", "Java", "WinRAR", "ProgramData",
        "regid.1991-06.com.microsoft", "Logs", "Log", "Cache", "Caches", "Crashpad", "Tools", "Users", "fontconfig", "Steam",
    };

    public void Run(ScanDatabase db, AnalysisWriter writer, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        // 1. Duplicates (reads file contents, so only with "look inside files").
        if (options.IdentifyContents && db.GetMeta("analysis.duplicates") != "done")
        {
            writer.ClearDuplicates();
            var groups = new DuplicateFinder(options.Gentle).Find(db, progress, ct);
            long groupId = 0;
            string users = tokens.Values.TryGetValue("users", out var u) ? u : Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
            foreach (var g in groups.Where(g => DuplicateFinder.IsMeaningful(g.Files, users)))
            {
                groupId++;
                var keeper = DuplicateFinder.ChooseKeeper(g.Files);
                foreach (var f in g.Files) writer.AddDuplicate(f.Id, groupId, f.Id == keeper.Id, g.Files.Count, keeper.Path);
            }
            writer.SetMeta("analysis.duplicates", "done");
            writer.Flush();
        }

        progress?.Report(new PipelineProgress(ScanPhase.Recommending, "Deciding what can go and what should stay…", null, null, null));
        var apps = db.GetApps().ToDictionary(a => a.Id);
        var appTokens = BuildAppTokens(apps.Values);
        var uninstall = SuggestUninstalls(db, writer, apps);
        var duplicates = LoadDuplicates(db);
        var appDataRoots = AppDataRoots();

        // 2. A verdict for every file and folder.
        var summary = db.GetSummary()!;
        long total = summary.FileCount + summary.DirectoryCount + 1, done = 0;
        var dirs = new Dictionary<long, (string? Rule, string Path, long Files, bool RuleHere)>();
        var modOf = new Dictionary<long, string>(); // folders that are, or are inside, a mod download -> the mod's name
        var clock = Stopwatch.StartNew();
        TimeSpan last = TimeSpan.Zero;
        var now = DateTime.UtcNow;

        using (var read = db.OpenReadConnection())
        {
            {
                foreach (var entry in ScanDatabase.ReadAllEntries(read))
                {
                    var a = entry.Analysis;
                    if (a is null) continue;
                    var rule = kb.GetRule(a.LocationRuleId);
                    bool matchedHere = entry.ParentId is long pid && dirs.TryGetValue(pid, out var parent)
                        ? !string.Equals(parent.Rule, a.LocationRuleId, StringComparison.Ordinal)
                        : rule is not null;

                    var flags = FactFlags.None;
                    string? shortcutTarget = null, keeper = null;
                    int copies = 0;
                    string? modName = null;
                    if (a.OwnerAppId is null && entry.ParentId is long mp && modOf.TryGetValue(mp, out var inMod))
                    {
                        flags |= FactFlags.InsideModDownload;
                        modName = inMod;
                    }

                    if (entry.IsDirectory)
                    {
                        dirs[entry.Id] = (a.LocationRuleId, entry.Path, entry.FileCount, matchedHere);
                        if (a.OwnerAppId is null && modName is null && ModDownloads.TryParse(entry.Name, isFolder: true, out var folderMod))
                        {
                            flags |= FactFlags.ModDownload;
                            modOf[entry.Id] = folderMod.Name;
                        }
                        else if (modName is not null) modOf[entry.Id] = modName;
                        bool parentHasFiles = entry.ParentId is long pp && dirs.TryGetValue(pp, out var pd) && pd.Files > 0;
                        if (entry.FileCount == 0 && entry.Depth > 0 && parentHasFiles && !entry.Flags.HasFlag(EntryFlags.EnumerationFailed)) flags |= FactFlags.EmptyFolder;
                        if (a.OwnerAppId is null && !matchedHere && entry.Path.LastIndexOf('\\') is int cut and > 0
                            && appDataRoots.Contains(entry.Path[..cut]) && IsLeftover(entry.Name, appTokens))
                            flags |= FactFlags.Leftover;
                    }
                    else
                    {
                        string ext = entry.Extension ?? "";
                        if (ext == ".lnk" && a.Protection < Protection.UninstallOnly && IsBroken(entry.Path, out shortcutTarget)) flags |= FactFlags.BrokenShortcut;
                        if (PartialDownloadExtensions.Contains(ext)) flags |= FactFlags.PartialDownload;
                        // An installer sitting in Downloads: directly in it, or clearly named as one in a subfolder. A program
                        // inside an unpacked folder (a portable program, a game build) is that program's own part, not an installer.
                        bool directlyInDownloads = entry.ParentId is long dp && dirs.TryGetValue(dp, out var dl) && dl.RuleHere && dl.Rule == "user.downloads";
                        if (a.LocationRuleId == "user.downloads" && a.OwnerAppId is null
                            && (a.Category == "installer" || ext is ".exe" or ".msi" && a.FormatId is null or "pe-exe" or "msi")
                            && (directlyInDownloads || ext == ".msi" || PortablePrograms.LooksLikeInstaller(entry.Name)))
                        {
                            flags |= FactFlags.InstallerInDownloads;
                            if (MatchesInstalled(entry.Name, appTokens)) flags |= FactFlags.MatchesInstalledProgram;
                        }
                        if (a.OwnerAppId is null && modName is null && ModDownloads.TryParse(entry.Name, isFolder: false, out _)) flags |= FactFlags.ModDownload;
                        if (duplicates.TryGetValue(entry.Id, out var dup))
                        {
                            flags |= FactFlags.Duplicate;
                            if (dup.IsKeeper) flags |= FactFlags.DuplicateKeeper;
                            keeper = dup.Keeper;
                            copies = dup.Copies;
                        }
                    }

                    var owner = a.OwnerAppId is long oid && apps.TryGetValue(oid, out var app) ? app : null;
                    var facts = new EntryFacts
                    {
                        Entry = entry,
                        Rule = rule,
                        RuleMatchedHere = matchedHere,
                        Owner = owner,
                        Flags = flags,
                        DuplicateKeeper = keeper,
                        DuplicateCopies = copies,
                        ShortcutTarget = shortcutTarget,
                        ContainedLastModifiedUtc = a.ContainedLastModifiedUtc,
                        OwnerUninstall = owner is not null && uninstall.TryGetValue(owner.Id, out var u) ? u : null,
                        NowUtc = now,
                        ModDownloadName = modName,
                    };
                    var verdict = Recommender.Evaluate(facts);
                    writer.SetRecommendation(entry.Id, verdict, verdict.NoEvidence ? flags | FactFlags.NoEvidence : flags);

                    if (++done % 2048 == 0) ct.ThrowIfCancellationRequested();
                    if (done % 2048 == 0 && clock.Elapsed - last > TimeSpan.FromMilliseconds(250))
                    {
                        last = clock.Elapsed;
                        progress?.Report(new PipelineProgress(ScanPhase.Recommending, "Deciding what can go and what should stay…",
                            $"{done:N0} of {total:N0} items", (double)done / total, entry.Path));
                    }
                }
            }
        }
        writer.Flush();

        // 3. Folders: how much inside can go, and verdicts for folders whose contents decide.
        RollUpFolders(db, writer, ct);
        writer.Flush();
    }

    // ------------------------------------------------------------------ folders

    private sealed class FolderAcc
    {
        public long? Parent;
        public long Reclaim, Review, Tool, Total, NotDeletableFiles;
        public Verdict? Weakest;
        public Dictionary<string, long> Reasons = new(StringComparer.Ordinal);
        public Recommendation? Own;
        public Protection Contained;
    }

    private static void RollUpFolders(ScanDatabase db, AnalysisWriter writer, CancellationToken ct)
    {
        var acc = new Dictionary<long, FolderAcc>();
        using (var read = db.OpenReadConnection())
        using (var cmd = read.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.id, e.parent_id, e.is_dir, e.size, a.verdict, a.action, a.headline, a.confidence, a.contained_protection
                FROM entry e JOIN analysis a ON a.entry_id = e.id ORDER BY e.id
                """;
            using var r = cmd.ExecuteReader();
            long n = 0;
            while (r.Read())
            {
                if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                long id = r.GetInt64(0);
                long? parent = r.IsDBNull(1) ? null : r.GetInt64(1);
                var verdict = r.IsDBNull(4) ? Verdict.Keep : (Verdict)r.GetInt32(4);
                var action = (CleanupAction)r.GetInt32(5);
                string headline = r.IsDBNull(6) ? "" : r.GetString(6);

                if (r.GetInt32(2) != 0)
                {
                    acc[id] = new FolderAcc
                    {
                        Parent = parent,
                        Own = new Recommendation(verdict, r.GetInt32(7), action, headline, []),
                        Contained = (Protection)r.GetInt32(8),
                    };
                    continue;
                }

                if (parent is not long p || !acc.TryGetValue(p, out var f)) continue;
                long size = r.GetInt64(3);
                f.Total += size;
                if (action == CleanupAction.UseWindowsTool) f.Tool += size;
                else if (verdict is Verdict.SafeToDelete or Verdict.RecommendDelete)
                {
                    f.Reclaim += size;
                    f.Weakest = f.Weakest is null || verdict < f.Weakest ? verdict : f.Weakest;
                    f.Reasons[headline] = f.Reasons.GetValueOrDefault(headline) + size;
                }
                else if (verdict == Verdict.Review) f.Review += size;
                if (verdict is not (Verdict.SafeToDelete or Verdict.RecommendDelete)) f.NotDeletableFiles++;
            }
        }

        // Deepest folders first, so each folder's totals include all its subfolders.
        foreach (long id in acc.Keys.OrderByDescending(k => k))
        {
            var f = acc[id];
            string? top = f.Reasons.Count == 0 ? null : f.Reasons.MaxBy(kv => kv.Value).Key;
            var own = f.Own!;
            var final = Recommender.ForFolderContents(own, f.Weakest, f.NotDeletableFiles == 0, f.Reclaim, f.Total, f.Contained, top);

            if (final.Verdict != own.Verdict || final.Headline != own.Headline)
            {
                var keptFlags = DbFlags(db, id);
                if (final.Verdict != Verdict.Keep) keptFlags &= ~FactFlags.NoEvidence; // the contents decided after all
                writer.SetRecommendation(id, final, keptFlags);
            }

            string? reasons = f.Reasons.Count == 0 ? null : string.Join("; ", f.Reasons.OrderByDescending(kv => kv.Value).Take(4)
                .Select(kv => $"{kv.Key}: {ByteSize.Format(kv.Value)}"));
            writer.SetFolderTotals(id, f.Reclaim, f.Review, f.Tool, reasons);

            if (f.Parent is long p && acc.TryGetValue(p, out var parent))
            {
                parent.Reclaim += f.Reclaim;
                parent.Review += f.Review;
                parent.Tool += f.Tool;
                parent.Total += f.Total;
                parent.NotDeletableFiles += f.NotDeletableFiles;
                if (f.Weakest is { } w) parent.Weakest = parent.Weakest is null || w < parent.Weakest ? w : parent.Weakest;
                foreach (var (k, v) in f.Reasons) parent.Reasons[k] = parent.Reasons.GetValueOrDefault(k) + v;
                if (parent.Reasons.Count > 12)
                    parent.Reasons = parent.Reasons.OrderByDescending(kv => kv.Value).Take(8).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            }
        }
    }

    private static FactFlags DbFlags(ScanDatabase db, long id) => db.GetEntry(id)?.Analysis?.Facts ?? FactFlags.None;

    // ------------------------------------------------------------------ programs

    /// <summary>Programs that look unused: not system parts or runtimes, not running anything in the background, and no sign of use for a long time.</summary>
    private static Dictionary<long, UninstallSuggestion> SuggestUninstalls(ScanDatabase db, AnalysisWriter writer, Dictionary<long, InstalledApp> apps)
    {
        var active = new HashSet<long>();
        using (var read = db.OpenReadConnection())
        using (var cmd = read.CreateCommand())
        {
            cmd.CommandText = $"SELECT owner_app_id FROM analysis WHERE owner_app_id IS NOT NULL GROUP BY owner_app_id HAVING MAX(protection) >= {(int)Protection.InUse}";
            using var r = cmd.ExecuteReader();
            while (r.Read()) active.Add(r.GetInt64(0));
        }

        bool usageReadable = db.GetUsageSources().Any(s => s.Source == UsageSource.ProgramLaunches && s.Available);
        var now = DateTime.UtcNow;
        var result = new Dictionary<long, UninstallSuggestion>();
        foreach (var app in apps.Values)
        {
            if (app.IsSystemComponent || app.IsFramework || active.Contains(app.Id)) continue;
            if (app.Source == AppSource.Portable) continue; // nothing to uninstall: shown as "keep or delete the folder as a whole"
            if (app.MeasuredBytes < 50L * 1024 * 1024) continue; // not worth suggesting
            if (app.InstallDate is { } installed && (now - installed).TotalDays < 90) continue;

            int confidence;
            string reason;
            if (app.LastUsedUtc is { } lastUsed)
            {
                double days = (now - lastUsed).TotalDays;
                if (days < 365) continue;
                confidence = days >= 730 ? 75 : 65;
                reason = $"Last used {Recommender.Days((int)days)} ago, and it takes {ByteSize.Format(app.MeasuredBytes)}.";
            }
            else
            {
                if (!usageReadable) continue;
                confidence = 55;
                string since = app.InstallDate is { } d ? $" since it was installed on {d.ToLocalTime():d}" : "";
                reason = $"No record of it being used{since}, and it takes {ByteSize.Format(app.MeasuredBytes)}. (Programs that only run in the background or are opened from other programs may not leave a record.)";
            }
            var s = new UninstallSuggestion(app.Id, confidence, reason);
            result[app.Id] = s;
            writer.SetUninstallSuggestion(app.Id, confidence, reason);
        }
        return result;
    }

    // ------------------------------------------------------------------ helpers

    private HashSet<string> AppDataRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string t in new[] { "programdata", "programfiles", "programfilesx86" })
            if (tokens.Values.TryGetValue(t, out var v)) roots.Add(v.TrimEnd('\\'));
        if (tokens.Values.TryGetValue("users", out var users) && Directory.Exists(users))
        {
            try
            {
                foreach (string profile in Directory.EnumerateDirectories(users))
                    foreach (string sub in new[] { "Roaming", "Local", "LocalLow" })
                        roots.Add(Path.Combine(profile, "AppData", sub));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return roots;
    }

    private static List<string> BuildAppTokens(IEnumerable<InstalledApp> apps)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in apps)
        {
            Add(a.Name);
            Add(a.Publisher);
            if (a.InstallLocation is { } loc) { Add(Path.GetFileName(loc)); Add(Path.GetFileName(Path.GetDirectoryName(loc) ?? "")); }
            if (a.Source == AppSource.StoreApp && a.SourceKey is { } family) Add(family.Split('_')[0]);
            foreach (string word in Words(a.Name)) Add(word);
        }
        return [.. tokens];

        void Add(string? s)
        {
            string n = Normalize(s);
            if (n.Length >= 4) tokens.Add(n);
        }
    }

    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "setup", "install", "installer", "windows", "win64", "win32", "x64", "x86", "amd64", "arm64", "full", "offline", "online", "web",
        "latest", "release", "version", "update", "updater", "microsoft", "corporation", "software", "program", "the", "for", "and",
        "edition", "tools", "free", "portable", "client", "desktop", "app", "application", "inc", "llc", "ltd", "company",
    };

    private static IEnumerable<string> Words(string? s) =>
        string.IsNullOrEmpty(s) ? [] :
        System.Text.RegularExpressions.Regex.Split(s, @"[^A-Za-z0-9]+|(?<=[a-z])(?=[A-Z])")
            .Select(w => w.ToLowerInvariant()).Where(w => w.Length >= 4 && !GenericWords.Contains(w) && !w.All(char.IsDigit));

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>A folder in AppData/ProgramData/Program Files no installed program's name, publisher or folder matches.</summary>
    private static bool IsLeftover(string folderName, List<string> appTokens)
    {
        if (NeverLeftover.Contains(folderName) || folderName.StartsWith('.') || folderName.StartsWith('{')) return false;
        string n = Normalize(folderName);
        if (n.Length < 3) return false;
        foreach (string t in appTokens)
        {
            if (n.Contains(t, StringComparison.Ordinal) || (n.Length >= 4 && t.Contains(n, StringComparison.Ordinal))) return false;
            if (n.Length >= 5 && t.Length >= 5 && n[..5] == t[..5]) return false;
        }
        return true;
    }

    private static bool MatchesInstalled(string fileName, List<string> appTokens)
    {
        foreach (string w in Words(Path.GetFileNameWithoutExtension(fileName)))
            if (w.Length >= 4 && appTokens.Any(t => t.Contains(w, StringComparison.Ordinal))) return true;
        return false;
    }

    /// <summary>A shortcut whose target is on a drive that's present, but the target itself is gone.</summary>
    private static bool IsBroken(string lnk, out string? target)
    {
        target = ShellLink.ReadTarget(lnk);
        if (target is null || target.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        string? root = Path.GetPathRoot(target);
        if (root is null || !Directory.Exists(root)) return false; // removable or network drive not connected: can't tell
        return !File.Exists(target) && !Directory.Exists(target);
    }

    private static Dictionary<long, (bool IsKeeper, string Keeper, int Copies)> LoadDuplicates(ScanDatabase db)
    {
        var map = new Dictionary<long, (bool, string, int)>();
        using var read = db.OpenReadConnection();
        using var cmd = read.CreateCommand();
        cmd.CommandText = "SELECT entry_id, is_keeper, keeper_path, copies FROM duplicate";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = (r.GetInt32(1) != 0, r.GetString(2), r.GetInt32(3));
        return map;
    }
}
