using PuterScanny.Core.Formatting;
using PuterScanny.Core.IO;
using PuterScanny.Core.Models;
using PuterScanny.Scanning;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

public sealed class ScanDatabaseTests
{
    [Fact]
    public void Scan_round_trips_through_sqlite()
    {
        using var tree = new TempTree();
        tree.File(@"big\movie.mp4", 300_000);
        tree.File(@"big\notes.txt", 20);
        string withStream = tree.File(@"small\setup.exe", 2_000);
        File.WriteAllText(withStream + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        using var dbFolder = new TempTree(); // kept outside the scanned tree so the scan never sees its own database
        string dbPath = Path.Combine(dbFolder.Root, "result.db");
        ScanSummary summary;
        using (var db = ScanDatabase.Create(dbPath))
            summary = new FileSystemScanner().Scan(new ScanOptions { RootPath = tree.Root }, db.CreateSink());

        using var read = ScanDatabase.Open(dbPath);
        var stored = read.GetSummary()!;
        Assert.Equal(ScanStatus.Completed, stored.Status);
        Assert.Equal(summary.FileCount, stored.FileCount);
        Assert.Equal(summary.TotalSize, stored.TotalSize);
        Assert.Equal(1, stored.StreamCount);

        var root = read.GetRoot()!;
        Assert.Equal(stored.TotalSize, root.TotalSize);

        // Children come back biggest first.
        var children = read.GetChildren(root.Id);
        Assert.Equal("big", children[0].Name);
        Assert.Equal(300_020, children[0].TotalSize);
        Assert.Equal(2, children[0].FileCount);

        var largest = read.GetLargestFiles(1);
        Assert.Equal("movie.mp4", largest[0].Name);

        var setup = read.GetChildren(children.Single(c => c.Name == "small").Id).Single();
        Assert.True(setup.Flags.HasFlag(EntryFlags.HasAlternateStreams));
        Assert.Equal("Zone.Identifier", read.GetStreams(setup.Id).Single().Name);

        var (more, moreSize) = read.GetChildrenStats(root.Id, skipLargest: 1);
        Assert.True(more >= 1);
        Assert.True(moreSize >= 2_000);
    }
}

public sealed class CoreTests
{
    [Theory]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"C:\Users", @"\\?\C:\Users")]
    [InlineData(@"\\server\share\x", @"\\?\UNC\server\share\x")]
    public void Long_path_prefix_round_trips(string display, string native)
    {
        Assert.Equal(native, LongPath.ToNative(display));
        Assert.Equal(display, LongPath.ToDisplay(native));
    }

    [Theory]
    [InlineData(@"c:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Windows\", @"C:\Windows")]
    public void Roots_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, LongPath.NormalizeRoot(input), ignoreCase: true);
    }

    [Fact]
    public void Combine_never_doubles_separators()
    {
        Assert.Equal(@"C:\a", LongPath.Combine(@"C:\", "a"));
        Assert.Equal(@"C:\x\a", LongPath.Combine(@"C:\x", "a"));
    }

    [Fact]
    public void Byte_sizes_read_like_file_explorer()
    {
        Assert.Equal("0 bytes", ByteSize.Format(0));
        Assert.Equal("1 byte", ByteSize.Format(1));
        Assert.Equal("1.00 KB", ByteSize.Format(1024).Replace(',', '.'));
        Assert.Equal("1.50 GB", ByteSize.Format(1024L * 1024 * 1024 * 3 / 2).Replace(',', '.'));
    }

    [Fact]
    public void Only_name_surrogate_reparse_points_are_skipped()
    {
        Assert.False(ReparseTags.ShouldDescend(ReparseTags.MountPoint));
        Assert.False(ReparseTags.ShouldDescend(ReparseTags.Symlink));
        Assert.True(ReparseTags.ShouldDescend(0x9000101A)); // OneDrive folder: real content, must be scanned
        Assert.True(ReparseTags.IsCloud(0x9000101A));
    }
}
