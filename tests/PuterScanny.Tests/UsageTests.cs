using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using PuterScanny.Analysis;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Signals.Usage;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

public sealed class UsageParsingTests
{
    [Fact]
    public void Shortcut_targets_are_read_from_the_file_itself()
    {
        using var tree = new TempTree();
        string target = tree.File(@"docs\report.pdf", 10);
        string lnk = Path.Combine(tree.Root, "report.lnk");

        // Make a real shortcut with Windows' own scripting object.
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(lnk);
        shortcut.TargetPath = target;
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);

        Assert.Equal(target, ShellLink.ReadTarget(lnk), ignoreCase: true);
        Assert.Null(ShellLink.ParseTarget(new byte[10]));
    }

    [Fact]
    public void Known_folder_names_in_launch_history_are_expanded()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(Path.Combine(pf, @"7-Zip\7zFM.exe"),
            UsageCollector.ExpandKnownFolder(@"{6D809377-6AF0-444B-8957-A3773F02200E}\7-Zip\7zFM.exe"), ignoreCase: true);
        Assert.Null(UsageCollector.ExpandKnownFolder("Microsoft.Windows.Explorer"));
    }

    private static byte[] DestEntry(int number, string path, DateTime when, int count, byte[]? extra = null)
    {
        var e = new byte[130 + path.Length * 2 + 4 + (extra?.Length ?? 0)];
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(88), number);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(100), when.ToFileTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(108), -1);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(116), count);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(128), (ushort)path.Length);
        Encoding.Unicode.GetBytes(path).CopyTo(e, 130);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(130 + path.Length * 2), extra?.Length ?? 0);
        extra?.CopyTo(e, 134 + path.Length * 2);
        return e;
    }

    [Fact]
    public void Jump_list_entries_with_and_without_extra_data_are_read()
    {
        var t1 = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 10, 9, 18, 30, 0, DateTimeKind.Utc);
        var header = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(header, 6);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 2);
        byte[] list = [.. header, .. DestEntry(1, @"D:\School\essay.docx", t1, 7, extra: new byte[57]), .. DestEntry(2, @"C:\Users\Me\notes.txt", t2, 2)];

        var entries = JumpLists.ParseDestList(list);
        Assert.Equal(2, entries.Count);
        Assert.Equal(@"D:\School\essay.docx", entries[0].Path);
        Assert.Equal(t1, entries[0].LastAccess);
        Assert.Equal(7, entries[0].Count);
        Assert.Equal(@"C:\Users\Me\notes.txt", entries[1].Path);
        Assert.Equal(t2, entries[1].LastAccess);
    }

    [Fact]
    public void Real_jump_lists_on_this_pc_parse_with_dates()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Recent), "AutomaticDestinations");
        if (!Directory.Exists(folder)) return;
        var items = Directory.EnumerateFiles(folder).SelectMany(JumpLists.Read).ToList();
        if (items.Count == 0) return;
        double dated = items.Count(i => i.LastAccessUtc is not null) / (double)items.Count;
        Assert.True(dated > 0.9, $"only {dated:P0} of jump list items have dates");
        Assert.All(items.Where(i => i.LastAccessUtc is not null), i => Assert.True(i.LastAccessUtc <= DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public void Usage_summary_takes_latest_date_and_largest_count()
    {
        var s = UsageIndex.Summarize(
        [
            new UsageEvent(UsageSource.RecentItems, new DateTime(2026, 1, 1), null, null, "a"),
            new UsageEvent(UsageSource.JumpLists, new DateTime(2026, 5, 1), 4, null, "b"),
            new UsageEvent(UsageSource.ProgramLaunches, new DateTime(2025, 1, 1), 9, TimeSpan.FromMinutes(30), "c"),
            new UsageEvent(UsageSource.ProgramLaunches, null, 2, TimeSpan.FromMinutes(10), "d"),
        ]);
        Assert.Equal(new DateTime(2026, 5, 1), s.LastUsedUtc);
        Assert.Equal(UsageSource.JumpLists, s.BestSource);
        Assert.Equal(9, s.UseCount);
        Assert.Equal(TimeSpan.FromMinutes(30), s.ActiveTime);
    }

    [Fact]
    public void Real_usage_history_can_be_read_without_administrator_rights()
    {
        var usage = UsageCollector.Collect([], null, default);
        Assert.Contains(usage.Sources, s => s.Source == UsageSource.RecentItems && s.Available);
        Assert.Contains(usage.Sources, s => s.Source == UsageSource.Prefetch);   // listed, even when it can't be read
        Assert.All(usage.ByPath.Values.SelectMany(v => v), e => Assert.True(e.LastUsedUtc is null || e.LastUsedUtc <= DateTime.UtcNow.AddDays(1)));
    }
}

public sealed class UsageAnalysisTests
{
    [Fact]
    public void Usage_lands_on_files_rolls_up_to_folders_and_shows_age()
    {
        using var tree = new TempTree();
        string essay = tree.File(@"school\term1\essay.docx", 100);
        string old = tree.File(@"school\term1\old-notes.txt", 100);
        tree.File(@"games\save.dat", 100);
        var recent = DateTime.UtcNow.AddDays(-3);
        var longAgo = DateTime.UtcNow.AddDays(-800);

        UsageIndex Fake(IReadOnlyList<InstalledApp> _)
        {
            var u = new UsageIndex();
            u.AddPath(essay, new UsageEvent(UsageSource.JumpLists, recent, 5, null, "Opened 5 times with Microsoft Word"));
            u.AddPath(essay, new UsageEvent(UsageSource.OfficeRecent, recent.AddHours(-2), null, null, "In Word's recent documents"));
            u.AddPath(old, new UsageEvent(UsageSource.RecentItems, longAgo, null, null, "Opened recently (Windows' Recent items)"));
            u.Sources.Add(new UsageSourceStatus(UsageSource.JumpLists, true, 1, ""));
            return u;
        }

        using var dbFolder = new TempTree();
        string dbPath = Path.Combine(dbFolder.Root, "scan.db");
        new ScanPipeline(KnowledgeBase.LoadDefault(), PathTokens.ForThisMachine(),
                new AnalysisOptions { IncludeMsiComponents = false, IdentifyContents = false }) { UsageSource = Fake }
            .Run(new ScanOptions { RootPath = tree.Root }, dbPath, null, default);

        using var db = ScanDatabase.Open(dbPath);
        var root = db.GetRoot()!;
        var school = db.GetChildren(root.Id).Single(c => c.Name == "school");
        var games = db.GetChildren(root.Id).Single(c => c.Name == "games");
        var term = db.GetChildren(school.Id).Single();
        var files = db.GetChildren(term.Id).ToDictionary(f => f.Name);

        var e = files["essay.docx"].Analysis!;
        Assert.Equal(recent, e.LastUsedUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(5, e.UseCount);
        Assert.Equal(2, db.GetUsage(files["essay.docx"].Id).Count);
        Assert.Equal(longAgo, files["old-notes.txt"].Analysis!.LastUsedUtc!.Value, TimeSpan.FromSeconds(1));

        // Folders know when anything inside was last used.
        Assert.Equal(recent, school.Analysis!.ContainedLastUsedUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(recent, root.Analysis!.ContainedLastUsedUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Null(games.Analysis!.ContainedLastUsedUtc);

        Assert.Equal(essay, db.GetRecentlyUsedFiles(10).First().Path);
        Assert.Contains(db.GetUsageSources(), s => s.Source == UsageSource.JumpLists);
    }
}
