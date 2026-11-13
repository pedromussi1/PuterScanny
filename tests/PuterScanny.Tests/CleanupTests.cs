using PuterScanny.Cleanup;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;

namespace PuterScanny.Tests;

/// <summary>Every test here works only on throwaway files it creates under %TEMP%.</summary>
public sealed class CleanupTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _app = new();   // stands in for %LOCALAPPDATA%\PuterScanny
    private readonly SafetyChecks _safety = new(PathTokens.ForThisMachine());
    private readonly Quarantine _quarantine;
    private readonly CleanupHistory _history;
    private readonly CleanupRunner _runner;

    public CleanupTests()
    {
        _quarantine = new Quarantine(Path.Combine(_app.Root, "Quarantine"));
        _history = new CleanupHistory(Path.Combine(_app.Root, "history.json"));
        _runner = new CleanupRunner(_safety, _quarantine, _history);
    }

    private static CleanupItem Item(string path, Verdict verdict = Verdict.SafeToDelete, CleanupAction action = CleanupAction.Delete)
    {
        bool dir = Directory.Exists(path);
        long size = dir ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : new FileInfo(path).Length;
        long files = dir ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Count() : 0;
        DateTime modified = dir
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()
            : File.GetLastWriteTimeUtc(path);
        return new CleanupItem(1, path, dir, size, modified, files, verdict, action, "test");
    }

    [Fact]
    public void Windows_programs_drives_and_key_folders_are_never_touched()
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(_safety.Blocked(Path.Combine(windir, "Temp", "x.tmp")));
        Assert.NotNull(_safety.Blocked(Path.Combine(windir, "System32")));
        Assert.NotNull(_safety.Blocked(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "App", "a.dll")));
        Assert.NotNull(_safety.Blocked(@"C:\"));
        Assert.NotNull(_safety.Blocked(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.NotNull(_safety.Blocked(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")));
        Assert.NotNull(_safety.Blocked(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")));
        Assert.Null(_safety.Blocked(_tree.File("ok.tmp", 10)));
    }

    [Fact]
    public void Only_items_rated_for_removal_and_unchanged_since_the_scan_pass()
    {
        string f = _tree.File("a.tmp", 100);
        Assert.Null(_safety.Check(Item(f)));
        Assert.NotNull(_safety.Check(Item(f, Verdict.Keep)));
        Assert.NotNull(_safety.Check(Item(f, Verdict.KeepPersonal)));
        Assert.NotNull(_safety.Check(Item(f, Verdict.Review, CleanupAction.Uninstall)));
        Assert.NotNull(_safety.Check(Item(f, Verdict.Review, CleanupAction.UseWindowsTool)));

        var before = Item(f);
        File.AppendAllText(f, "changed");
        Assert.Contains("changed", _safety.Check(before));

        string folder = _tree.Dir("old");
        File.WriteAllText(Path.Combine(folder, "1.txt"), "x");
        var folderItem = Item(folder);
        for (int i = 0; i < 10; i++) File.WriteAllText(Path.Combine(folder, $"new{i}.txt"), "new");
        Assert.NotNull(_safety.Check(folderItem));
    }

    [Fact]
    public void Files_open_in_another_program_are_left_alone()
    {
        string f = _tree.File("busy.log", 100);
        var item = Item(f);
        using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            // Opened by this very process: Restart Manager names it.
            Assert.Contains("In use", _safety.Check(item));
        }
        Assert.Null(_safety.Check(item));
    }

    [Fact]
    public void Quarantine_removes_and_undo_puts_everything_back()
    {
        string a = _tree.File(@"junk\a.tmp", 1000);
        string b = _tree.File("b.tmp", 2000);
        string folder = Path.GetDirectoryName(a)!;

        var batch = _runner.Run([Item(folder), Item(b), Item(a)], RemovalMethod.Quarantine, null, null, default);
        Assert.Equal(2, batch.RemovedCount);                            // a.tmp is inside the folder, so it goes with it
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(b));
        Assert.All(batch.Items, i => Assert.True(_quarantine.IsInsideQuarantine(i.StoredAt!)));
        Assert.True(_runner.QuarantinedBytes() >= 3000);

        // The history survives a restart of the app.
        var reloaded = new CleanupHistory(_history.FilePath);
        Assert.Single(reloaded.Batches);

        var (restored, problems, _) = _runner.Undo(batch.Id);
        Assert.Equal(2, restored);
        Assert.Equal(0, problems);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal(0, _runner.QuarantinedBytes());
    }

    [Fact]
    public void Undo_never_overwrites_something_new()
    {
        string f = _tree.File("report.tmp", 10);
        var batch = _runner.Run([Item(f)], RemovalMethod.Quarantine, null, null, default);
        File.WriteAllText(f, "a new file with the same name");
        _runner.Undo(batch.Id);
        Assert.Equal("a new file with the same name", File.ReadAllText(f));
        Assert.True(File.Exists(Path.Combine(_tree.Root, "report (restored).tmp")));
    }

    [Fact]
    public void Emptying_the_quarantine_deletes_only_what_is_inside_it()
    {
        string f = _tree.File("gone.tmp", 500);
        var batch = _runner.Run([Item(f)], RemovalMethod.Quarantine, null, null, default);
        string stored = batch.Items[0].StoredAt!;
        Assert.Equal((0, 0L), _runner.EmptyQuarantine(olderThanDays: 30)); // too recent
        var (purged, bytes) = _runner.EmptyQuarantine();
        Assert.Equal(1, purged);
        Assert.Equal(500, bytes);
        Assert.False(File.Exists(stored));
        Assert.Throws<InvalidOperationException>(() => _quarantine.Purge(_tree.File("outside.txt", 1)));
    }

    [Fact]
    public void Skipped_items_say_why_and_are_never_touched()
    {
        string f = _tree.File("keep.txt", 10);
        var batch = _runner.Run([Item(f, Verdict.Keep)], RemovalMethod.Quarantine, null, null, default);
        Assert.Equal(ItemStatus.Skipped, batch.Items[0].Status);
        Assert.False(string.IsNullOrWhiteSpace(batch.Items[0].Note));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Recycle_bin_round_trip()
    {
        string f = _tree.File($"puterscanny-test-{Guid.NewGuid():N}.tmp", 64);
        var batch = _runner.Run([Item(f)], RemovalMethod.RecycleBin, null, null, default);
        var rec = batch.Items[0];
        try
        {
            Assert.Equal(ItemStatus.Removed, rec.Status);
            Assert.False(File.Exists(f));
            Assert.NotNull(rec.StoredAt);   // found its $R file
            Assert.NotNull(rec.RecycleInfoFile);
            Assert.Equal(f, RecycleBin.ReadInfo(rec.RecycleInfoFile!)!.Value.OriginalPath, ignoreCase: true);

            var (restored, _, _) = _runner.Undo(batch.Id);
            Assert.Equal(1, restored);
            Assert.True(File.Exists(f));
        }
        finally
        {
            // Never leave test files in the real Recycle Bin.
            if (rec.StoredAt is not null && File.Exists(rec.StoredAt)) File.Delete(rec.StoredAt);
            if (rec.RecycleInfoFile is not null && File.Exists(rec.RecycleInfoFile)) File.Delete(rec.RecycleInfoFile);
        }
    }

    public void Dispose()
    {
        _tree.Dispose();
        _app.Dispose();
    }
}
