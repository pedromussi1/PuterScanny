using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PuterScanny.Analysis;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

public sealed class ResumeTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();

    private static ScanPipeline Pipeline() =>
        new(Kb, PathTokens.ForThisMachine(), new AnalysisOptions { IncludeMsiComponents = false, Gentle = true });

    private static (TempTree Tree, TempTree Scans, string Db) ScanSample()
    {
        var tree = new TempTree();
        for (int i = 0; i < 40; i++)
            File.WriteAllBytes(Path.Combine(tree.Root, $"pic{i}.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, (byte)i]);
        tree.File(@"docs\notes.txt", 50);
        var scans = new TempTree();
        string db = ScanDatabase.NewScanFilePath(scans.Root);
        Pipeline().Run(new ScanOptions { RootPath = tree.Root }, db, null, default);
        return (tree, scans, db);
    }

    /// <summary>Makes a finished scan look as if Windows crashed halfway through checking files.</summary>
    private static void SimulateCrash(string db, bool duringOwnership = false)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = duringOwnership
            ? "UPDATE meta SET value='running' WHERE key='analysis.status'; DELETE FROM meta WHERE key='analysis.owners'; DELETE FROM analysis WHERE entry_id % 2 = 0;"
            : "UPDATE meta SET value='running' WHERE key='analysis.status'; " +
              "UPDATE analysis SET read_status = 0, format_id = NULL, type_label = 'PNG image (from name only)' " +
              "WHERE entry_id IN (SELECT id FROM entry WHERE is_dir = 0 AND id % 2 = 0);";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void An_interrupted_check_is_found_and_finished_without_redoing_work()
    {
        var (tree, scans, db) = ScanSample();
        using (tree) using (scans)
        {
            Assert.Null(InterruptedScans.Find(tree.Root, scans.Root)); // finished scans are not offered

            SimulateCrash(db);
            var found = InterruptedScans.Find(tree.Root, scans.Root);
            Assert.NotNull(found);
            Assert.Equal(db, found.FilePath);
            Assert.True(found.OwnershipDone);
            Assert.Equal(41, found.Files);
            Assert.InRange(found.FilesChecked, 1, 40);

            Pipeline().Resume(db, null, default);

            using var read = ScanDatabase.Open(db);
            Assert.Equal("complete", read.GetMeta("analysis.status"));
            Assert.Equal("1", read.GetMeta("analysis.resumed"));
            Assert.Equal(41, read.CountIdentifiedFiles());
            Assert.All(read.GetChildren(read.GetRoot()!.Id).Where(e => e.Name.EndsWith(".png")),
                e => Assert.Equal("PNG image", e.Analysis!.TypeLabel));
            Assert.Null(InterruptedScans.Find(tree.Root, scans.Root));
        }
    }

    [Fact]
    public void A_crash_during_ownership_matching_redoes_that_stage_cleanly()
    {
        var (tree, scans, db) = ScanSample();
        using (tree) using (scans)
        {
            SimulateCrash(db, duringOwnership: true);
            var found = InterruptedScans.Find(tree.Root, scans.Root);
            Assert.NotNull(found);
            Assert.False(found.OwnershipDone);

            Pipeline().Resume(db, null, default);

            using var read = ScanDatabase.Open(db);
            Assert.Equal("complete", read.GetMeta("analysis.status"));
            Assert.Equal(41, read.CountIdentifiedFiles());
            Assert.Equal("user.temp", read.GetRoot()!.Analysis!.LocationRuleId);
        }
    }

    [Fact]
    public void Declined_scans_are_not_offered_again()
    {
        var (tree, scans, db) = ScanSample();
        using (tree) using (scans)
        {
            SimulateCrash(db);
            Assert.NotNull(InterruptedScans.Find(tree.Root, scans.Root));
            InterruptedScans.Decline(db);
            Assert.Null(InterruptedScans.Find(tree.Root, scans.Root));
        }
    }

    [Fact]
    public void Rate_limiter_spaces_work_evenly()
    {
        var limiter = new RateLimiter(100);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 51; i++) limiter.Wait(default);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.45, 2.0); // 50 intervals of 10 ms
    }

    [Fact]
    public void Rate_limiter_does_not_burst_after_a_slow_item()
    {
        var limiter = new RateLimiter(100);
        limiter.Wait(default);
        Thread.Sleep(300); // a slow file: the next ones must not rush through 30 at once
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 11; i++) limiter.Wait(default);
        Assert.True(clock.Elapsed.TotalSeconds >= 0.09, $"burst: {clock.Elapsed.TotalMilliseconds} ms");
    }
}
