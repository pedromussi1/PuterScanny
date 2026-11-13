using PuterScanny.Analysis;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

public sealed class RecommenderTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private const string Profile = @"C:\Users\Sam";

    private static FileSystemEntry Entry(string path, bool dir = false, int ageDays = 100, AnalysisInfo? a = null, EntryFlags flags = EntryFlags.None, long size = 1000) => new()
    {
        Id = 10,
        ParentId = 9,
        Name = Path.GetFileName(path),
        Path = path,
        IsDirectory = dir,
        Depth = 4,
        Size = dir ? 0 : size,
        TotalSize = size,
        CreatedUtc = Now.AddDays(-ageDays - 1),
        ModifiedUtc = Now.AddDays(-ageDays),
        FileCount = dir ? 3 : 0,
        Flags = flags,
        Analysis = a ?? new AnalysisInfo(),
    };

    private static Recommendation Eval(FileSystemEntry e, string? rule = null, bool here = false, FactFlags facts = FactFlags.None,
        string? keeper = null, UninstallSuggestion? uninstall = null, InstalledApp? owner = null) =>
        Recommender.Evaluate(new EntryFacts
        {
            Entry = e,
            Rule = rule is null ? null : Kb.GetRule(rule) ?? throw new ArgumentException(rule),
            RuleMatchedHere = here,
            Flags = facts,
            DuplicateKeeper = keeper,
            DuplicateCopies = keeper is null ? 0 : 2,
            OwnerUninstall = uninstall,
            Owner = owner,
            NowUtc = Now,
        });

    [Fact]
    public void Hard_vetoes_win_over_everything()
    {
        var required = Eval(Entry(@"C:\Windows\Temp\x.tmp", a: new AnalysisInfo { Protection = Protection.Required, Category = "temporary" }), "win.temp");
        Assert.Equal(Verdict.Required, required.Verdict);

        var inUse = Eval(Entry(Profile + @"\AppData\Local\Temp\a.log", a: new AnalysisInfo { Protection = Protection.InUse }), "user.temp");
        Assert.Equal(Verdict.Keep, inUse.Verdict);

        var program = Eval(Entry(@"C:\Program Files\App\data.cache", a: new AnalysisInfo { Protection = Protection.UninstallOnly, OwnerAppName = "App", Category = "cache" }), "win.programfiles");
        Assert.Equal(Verdict.Keep, program.Verdict);
        Assert.Contains("App", program.Headline);
    }

    [Fact]
    public void Unused_programs_are_suggested_for_uninstalling_not_deleting()
    {
        var folder = Entry(@"C:\Program Files\OldApp", dir: true,
            a: new AnalysisInfo { Protection = Protection.UninstallOnly, OwnerAppName = "OldApp", OwnerAppId = 5, OwnerIsExact = true });
        var r = Eval(folder, "win.programfiles", uninstall: new UninstallSuggestion(5, 75, "Last used 3 years ago."));
        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Equal(CleanupAction.Uninstall, r.Action);
    }

    [Fact]
    public void Old_temporary_files_are_recommended_and_new_ones_kept()
    {
        Assert.Equal(Verdict.RecommendDelete, Eval(Entry(Profile + @"\AppData\Local\Temp\setup123.tmp", ageDays: 30), "user.temp").Verdict);
        var fresh = Eval(Entry(Profile + @"\AppData\Local\Temp\setup123.tmp", ageDays: 2), "user.temp");
        Assert.Equal(Verdict.Keep, fresh.Verdict);
        Assert.Contains("recently", fresh.Headline);
    }

    [Fact]
    public void A_cleanable_folder_itself_stays_unless_the_whole_folder_may_go()
    {
        var temp = Eval(Entry(Profile + @"\AppData\Local\Temp", dir: true, ageDays: 300), "user.temp", here: true);
        Assert.Equal(Verdict.Keep, temp.Verdict);

        var modules = Eval(Entry(@"D:\code\site\node_modules", dir: true, ageDays: 200), "dev.node_modules", here: true);
        Assert.Equal(Verdict.SafeToDelete, modules.Verdict);

        var busy = Eval(Entry(@"D:\code\site\node_modules", dir: true, ageDays: 200, a: new AnalysisInfo { ContainedProtection = Protection.InUse }), "dev.node_modules", here: true);
        Assert.Equal(Verdict.Keep, busy.Verdict);

        var recentProject = Eval(Entry(@"D:\code\site\node_modules", dir: true, ageDays: 10), "dev.node_modules", here: true);
        Assert.Equal(Verdict.Keep, recentProject.Verdict);
    }

    [Fact]
    public void Your_files_are_never_more_than_review()
    {
        var photo = Entry(Profile + @"\Pictures\beach.jpg", a: new AnalysisInfo { Category = "image" });
        Assert.Equal(Verdict.KeepPersonal, Eval(photo, "user.pictures").Verdict);

        var copy = Eval(photo, "user.pictures", facts: FactFlags.Duplicate, keeper: Profile + @"\Pictures\Trip\beach.jpg");
        Assert.Equal(Verdict.Review, copy.Verdict);
        Assert.Equal(CleanupAction.KeepOneCopy, copy.Action);

        var oldDoc = Eval(Entry(@"D:\Stuff\old.bak", ageDays: 2000, a: new AnalysisInfo { Category = "document" }));
        Assert.Equal(Verdict.KeepPersonal, oldDoc.Verdict);
    }

    [Fact]
    public void Downloads_installers_partials_and_broken_shortcuts()
    {
        var installer = Entry(Profile + @"\Downloads\ChromeSetup.exe", ageDays: 60, a: new AnalysisInfo { Category = "installer" });
        Assert.Equal(Verdict.RecommendDelete, Eval(installer, "user.downloads", facts: FactFlags.InstallerInDownloads | FactFlags.MatchesInstalledProgram).Verdict);
        Assert.Equal(Verdict.SafeToDelete, Eval(installer, "user.downloads", facts: FactFlags.InstallerInDownloads).Verdict);
        var fresh = Entry(Profile + @"\Downloads\ChromeSetup.exe", ageDays: 3, a: new AnalysisInfo { Category = "installer" });
        Assert.False(Eval(fresh, "user.downloads", facts: FactFlags.InstallerInDownloads).IsDeletable);

        Assert.Equal(Verdict.RecommendDelete, Eval(Entry(Profile + @"\Downloads\movie.mp4.crdownload", ageDays: 10), "user.downloads", facts: FactFlags.PartialDownload).Verdict);
        Assert.Equal(Verdict.Keep, Eval(Entry(Profile + @"\Downloads\movie.mp4.crdownload", ageDays: 0), "user.downloads", facts: FactFlags.PartialDownload).Verdict);

        Assert.Equal(Verdict.RecommendDelete, Eval(Entry(Profile + @"\Desktop\Old Game.lnk", a: new AnalysisInfo { Category = "shortcut" }), "user.desktop", facts: FactFlags.BrokenShortcut).Verdict);
    }

    [Fact]
    public void Old_downloaded_archives_and_disk_images_are_offered_for_review()
    {
        var iso = Entry(Profile + @"\Downloads\linux.iso", ageDays: 200, a: new AnalysisInfo { Category = "disk-image" });
        var r = Eval(iso, "user.downloads");
        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Equal(CleanupAction.Delete, r.Action);
        var fresh = Entry(Profile + @"\Downloads\linux.iso", ageDays: 20, a: new AnalysisInfo { Category = "disk-image" });
        Assert.Equal(Verdict.Keep, Eval(fresh, "user.downloads").Verdict);
    }

    [Fact]
    public void Leftovers_are_only_ever_offered_for_review()
    {
        var old = Eval(Entry(Profile + @"\AppData\Roaming\RemovedApp", dir: true, ageDays: 900), "user.appdata.roaming", facts: FactFlags.Leftover);
        Assert.Equal(Verdict.Review, old.Verdict);
        Assert.True(old.Confidence >= 65);
        var recent = Eval(Entry(Profile + @"\AppData\Roaming\RemovedApp", dir: true, ageDays: 20), "user.appdata.roaming", facts: FactFlags.Leftover);
        Assert.Equal(Verdict.Review, recent.Verdict);
        Assert.True(recent.Confidence < old.Confidence);
    }

    [Fact]
    public void Windows_tools_and_app_managed_data()
    {
        var winOld = Eval(Entry(@"C:\Windows.old\Users\x.dat", ageDays: 20), "win.old");
        Assert.Equal(CleanupAction.UseWindowsTool, winOld.Action);
        Assert.False(winOld.IsDeletable);

        var docker = Eval(Entry(Profile + @"\AppData\Local\Docker\wsl\disk.vhdx", ageDays: 400), "dev.docker");
        Assert.Equal(CleanupAction.ClearInApp, docker.Action);
        Assert.Equal(Verdict.Keep, docker.Verdict);
    }

    [Fact]
    public void Online_only_files_and_links_are_kept()
    {
        Assert.Equal(Verdict.Keep, Eval(Entry(Profile + @"\OneDrive\big.mp4", flags: EntryFlags.CloudPlaceholder, ageDays: 900, a: new AnalysisInfo { Category = "video" }), "user.onedrive").Verdict);
        Assert.Equal(Verdict.Keep, Eval(Entry(Profile + @"\AppData\Local\Temp\link", dir: true, flags: EntryFlags.ReparsePointNotFollowed), "user.temp").Verdict);
    }

    [Fact]
    public void Empty_folders_outside_program_data_can_go()
    {
        Assert.Equal(Verdict.SafeToDelete, Eval(Entry(Profile + @"\Documents\New folder", dir: true), "user.documents", facts: FactFlags.EmptyFolder).Verdict);
        Assert.Equal(Verdict.Keep, Eval(Entry(Profile + @"\AppData\Local\SomeApp\Logs", dir: true), "user.appdata.local", facts: FactFlags.EmptyFolder).Verdict);
    }

    [Fact]
    public void Recent_use_holds_back_otherwise_deletable_files()
    {
        var used = Entry(@"D:\Work\cache\thing.tmp", ageDays: 60, a: new AnalysisInfo { Category = "temporary", LastUsedUtc = Now.AddDays(-2) });
        Assert.Equal(Verdict.Review, Eval(used).Verdict);
        var unused = Entry(@"D:\Work\cache\thing.tmp", ageDays: 60, a: new AnalysisInfo { Category = "temporary" });
        Assert.Equal(Verdict.SafeToDelete, Eval(unused).Verdict);
    }

    [Fact]
    public void Safety_sweep_no_combination_breaks_the_guarantees()
    {
        string?[] rules = [null, "user.temp", "win.temp", "browser.chromium.cache", "dev.node_modules", "user.downloads", "user.pictures",
                           "user.onedrive", "win.old", "win.system32", "user.appdata.roaming", "dev.npm.cache", "user.documents", "win.programfiles"];
        string?[] categories = [null, "temporary", "cache", "crash-dump", "log", "backup", "image", "document", "video", "installer", "executable", "other"];
        FactFlags[] factSets =
        [
            FactFlags.None, FactFlags.EmptyFolder, FactFlags.BrokenShortcut, FactFlags.Leftover, FactFlags.InstallerInDownloads | FactFlags.MatchesInstalledProgram,
            FactFlags.PartialDownload, FactFlags.Duplicate,
        ];
        int checkedCount = 0;

        foreach (string? rule in rules)
        foreach (string? category in categories)
        foreach (var facts in factSets)
        foreach (Protection protection in Enum.GetValues<Protection>())
        foreach (Protection contained in new[] { Protection.None, Protection.Caution, Protection.InUse })
        foreach (bool dir in new[] { false, true })
        foreach (bool here in new[] { false, true })
        foreach (int age in new[] { 0, 5, 40, 400, 3000 })
        foreach (EntryFlags flags in new[] { EntryFlags.None, EntryFlags.CloudPlaceholder })
        {
            string path = rule switch
            {
                "user.pictures" => Profile + @"\Pictures\x",
                "user.documents" => Profile + @"\Documents\x",
                "user.downloads" => Profile + @"\Downloads\x",
                "user.onedrive" => Profile + @"\OneDrive\x",
                null => @"D:\Data\x",
                _ => Profile + @"\AppData\Local\Temp\x",
            };
            var a = new AnalysisInfo { Protection = protection, ContainedProtection = dir ? contained : Protection.None, Category = dir ? null : category, OwnerAppName = "App" };
            var e = Entry(path, dir, age, a, flags);
            var r = Eval(e, rule, here, facts, keeper: facts.HasFlag(FactFlags.Duplicate) ? @"D:\Data\y" : null);
            checkedCount++;

            string ctx = $"rule={rule} cat={category} facts={facts} prot={protection} contained={contained} dir={dir} here={here} age={age} flags={flags} → {r.Verdict} ({r.Headline})";
            if (protection >= Protection.UninstallOnly) Assert.False(r.IsDeletable, ctx);
            if (protection >= Protection.InUse) Assert.True(r.Verdict is Verdict.Required or Verdict.Keep, ctx);
            if (protection == Protection.Caution) Assert.False(r.IsDeletable, ctx);
            if (flags == EntryFlags.CloudPlaceholder && protection < Protection.InUse) Assert.Equal(Verdict.Keep, r.Verdict);
            if (dir && contained >= Protection.UninstallOnly) Assert.False(r.IsDeletable, ctx);
            if (dir && contained == Protection.Caution) Assert.False(r.IsDeletable, ctx);
            bool personalFile = !dir && rule is "user.pictures" or "user.documents" or "user.onedrive" or null
                                && category is "image" or "document" or "video";
            if (personalFile) Assert.False(r.IsDeletable, ctx);
            Assert.InRange(r.Confidence, 1, 100);
            Assert.False(string.IsNullOrWhiteSpace(r.Headline), ctx);
            Assert.NotEmpty(r.Evidence);
        }
        Assert.True(checkedCount > 100_000);
    }
}

[Trait("Category", "Safety")]
public sealed class RecommendationPipelineTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();

    private static ScanDatabase Scan(string root, TempTree dbFolder, bool lookInside)
    {
        string db = Path.Combine(dbFolder.Root, "scan.db");
        new ScanPipeline(Kb, PathTokens.ForThisMachine(),
                new AnalysisOptions { IncludeMsiComponents = false, IdentifyContents = lookInside, VerifySignatures = false, Gentle = false })
            .Run(new ScanOptions { RootPath = root, IncludeAlternateStreams = false }, db, null, default);
        return ScanDatabase.Open(db);
    }

    [Fact]
    public void Nothing_in_the_drivers_folder_is_ever_suggested_for_deletion()
    {
        using var dbFolder = new TempTree();
        using var db = Scan(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers"), dbFolder, lookInside: false);
        var stats = db.GetVerdictStats();
        Assert.NotEmpty(stats);
        Assert.All(stats, s => Assert.True(s.Verdict is Verdict.Required or Verdict.Keep, $"{s.Files} files got {s.Verdict}"));
    }

    [Fact]
    public void Temp_files_duplicates_and_folders_get_sensible_verdicts()
    {
        using var tree = new TempTree();   // under %TEMP%, a known clean-up location
        string old = tree.File(@"old\leftover.log", 500);
        string fresh = tree.File(@"fresh.txt", 500);
        byte[] png = new byte[70 * 1024];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        new Random(7).NextBytes(png.AsSpan(16));
        File.WriteAllBytes(Path.Combine(tree.Root, "pic-a.png"), png);
        Directory.CreateDirectory(Path.Combine(tree.Root, "copies"));
        File.WriteAllBytes(Path.Combine(tree.Root, "copies", "pic-b.png"), png);
        foreach (string f in new[] { old, Path.Combine(tree.Root, "pic-a.png"), Path.Combine(tree.Root, "copies", "pic-b.png") })
        {
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-40));
            File.SetCreationTimeUtc(f, DateTime.UtcNow.AddDays(-40));
        }
        Directory.SetLastWriteTimeUtc(Path.Combine(tree.Root, "old"), DateTime.UtcNow.AddDays(-40));
        Directory.SetCreationTimeUtc(Path.Combine(tree.Root, "old"), DateTime.UtcNow.AddDays(-40));

        using var dbFolder = new TempTree();
        using var db = Scan(tree.Root, dbFolder, lookInside: true);
        var root = db.GetRoot()!;
        var children = db.GetChildren(root.Id).ToDictionary(c => c.Name);

        Assert.Equal(Verdict.Keep, children["fresh.txt"].Analysis!.Verdict);                      // too new to clean
        var oldFolder = children["old"];
        Assert.Equal(Verdict.RecommendDelete, oldFolder.Analysis!.Verdict);                        // whole old temp folder can go
        Assert.True(root.Analysis!.ReclaimBytes >= 500);

        var dupes = db.GetDuplicateGroups(10);
        var group = Assert.Single(dupes);
        Assert.Equal(2, group.Copies);
        var dupEntries = new[] { children["pic-a.png"], db.GetChildren(children["copies"].Id).Single() };
        Assert.Single(dupEntries, d => d.Analysis!.Facts.HasFlag(FactFlags.DuplicateKeeper));
        Assert.Single(dupEntries, d => d.Analysis!.Action == CleanupAction.KeepOneCopy);

        // Every stored verdict can be re-explained with reasons.
        foreach (var e in db.GetChildren(root.Id))
        {
            var why = RecommendationExplainer.Explain(db, Kb, e);
            Assert.NotNull(why);
            Assert.NotEmpty(why.Evidence);
            Assert.Equal(e.Analysis!.Verdict, why.Verdict);
        }
    }
}
