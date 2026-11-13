using PuterScanny.Core.Models;
using PuterScanny.Scanning;

namespace PuterScanny.Tests;

public sealed class FileSystemScannerTests
{
    private static ListSink Scan(string root, CancellationToken ct = default)
    {
        var sink = new ListSink();
        new FileSystemScanner().Scan(new ScanOptions { RootPath = root }, sink, null, ct);
        return sink;
    }

    [Fact]
    public void Finds_every_file_and_folder_and_adds_up_totals()
    {
        using var tree = new TempTree();
        tree.File("a.txt", 100);
        tree.File(@"docs\b.bin", 5_000);
        tree.File(@"docs\deep\er\c.dat", 70_000);
        tree.Dir(@"empty");

        var sink = Scan(tree.Root);

        Assert.Equal(ScanStatus.Completed, sink.Final!.Status);
        Assert.Equal(3, sink.Final.FileCount);
        Assert.Equal(4, sink.Final.DirectoryCount); // docs, docs\deep, docs\deep\er, empty
        Assert.Equal(75_100, sink.Final.TotalSize);

        var docs = sink.Find("docs");
        Assert.Equal(75_000, sink.Totals[docs.Id].TotalSize);
        Assert.Equal(2, sink.Totals[docs.Id].FileCount);
        Assert.Equal(2, sink.Totals[docs.Id].DirectoryCount);
        Assert.Equal(0, sink.Totals[sink.Find("empty").Id].TotalSize);

        // Every entry has a parent that was emitted earlier.
        var seen = new HashSet<long>();
        foreach (var e in sink.Entries)
        {
            if (e.ParentId is long p) Assert.Contains(p, seen);
            seen.Add(e.Id);
        }
    }

    [Fact]
    public void Size_on_disk_is_rounded_to_whole_clusters()
    {
        using var tree = new TempTree();
        tree.File("small.txt", 10);
        var sink = Scan(tree.Root);
        var file = sink.Find("small.txt");
        Assert.True(file.SizeOnDisk >= file.Size);
        Assert.Equal(0, file.SizeOnDisk % sink.Final!.ClusterSize);
        Assert.True(file.Flags.HasFlag(EntryFlags.SizeOnDiskEstimated));
    }

    [Fact]
    public void Junctions_are_recorded_but_never_followed()
    {
        using var tree = new TempTree();
        tree.File(@"real\payload.bin", 10_000);
        tree.Junction("link", "real");

        var sink = Scan(tree.Root);

        var link = sink.Find("link");
        Assert.True(link.IsDirectory);
        Assert.True(link.Flags.HasFlag(EntryFlags.ReparsePointNotFollowed));
        Assert.Equal(ReparseTags.MountPoint, link.ReparseTag);
        Assert.EndsWith("real", link.LinkTarget);
        Assert.Single(sink.Entries, e => e.Name == "payload.bin");   // counted once, via the real folder
        Assert.Equal(10_000, sink.Final!.TotalSize);
        Assert.Contains(sink.Issues, i => i.Kind == ScanIssueKind.LinkNotFollowed && i.Path == link.Path);
    }

    [Fact]
    public void Hidden_alternate_data_streams_are_found()
    {
        using var tree = new TempTree();
        string file = tree.File("downloaded.exe", 1_000);
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        var sink = Scan(tree.Root);

        var entry = sink.Find("downloaded.exe");
        Assert.True(entry.Flags.HasFlag(EntryFlags.HasAlternateStreams));
        var stream = Assert.Single(sink.Streams, s => s.EntryId == entry.Id);
        Assert.Equal("Zone.Identifier", stream.Name);
        Assert.True(stream.Size > 0);
        Assert.Equal(1_000, entry.Size); // stream bytes are reported separately, not mixed into the size
    }

    [Fact]
    public void Paths_longer_than_260_characters_are_scanned()
    {
        using var tree = new TempTree();
        string relative = string.Join('\\', Enumerable.Repeat("a_fairly_long_folder_name_here", 12)) + @"\deep.txt";
        Directory.CreateDirectory(@"\\?\" + Path.GetDirectoryName(Path.Combine(tree.Root, relative)));
        File.WriteAllBytes(@"\\?\" + Path.Combine(tree.Root, relative), new byte[42]);

        var sink = Scan(tree.Root);

        var deep = sink.Find("deep.txt");
        Assert.True(deep.Path.Length > 260);
        Assert.Equal(42, deep.Size);
        Assert.Empty(sink.Issues);
    }

    [Fact]
    public void Cancelling_stops_the_scan_and_still_reports_a_summary()
    {
        using var tree = new TempTree();
        tree.File(@"x\y.txt", 5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sink = Scan(tree.Root, cts.Token);

        Assert.Equal(ScanStatus.Cancelled, sink.Final!.Status);
        Assert.NotNull(sink.Final.CompletedUtc);
    }

    [Fact]
    public void Missing_root_is_rejected()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Scan(@"C:\definitely\not\here\" + Guid.NewGuid()));
    }

    [Fact]
    public void Inaccessible_system_folder_becomes_an_issue_not_a_crash()
    {
        // "System Volume Information" exists on every NTFS volume and is locked to SYSTEM.
        string svi = @"C:\System Volume Information";
        if (!Directory.Exists(svi) || ElevationInfo.IsProcessElevated()) return;

        var sink = new ListSink();
        new FileSystemScanner().Scan(new ScanOptions { RootPath = svi }, sink);

        Assert.Equal(ScanStatus.Completed, sink.Final!.Status);
        Assert.Contains(sink.Issues, i => i.Kind == ScanIssueKind.AccessDenied);
        Assert.True(sink.Totals[1].ExtraFlags.HasFlag(EntryFlags.EnumerationFailed));
    }
}
