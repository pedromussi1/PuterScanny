using System.Buffers.Binary;
using System.Text;
using PuterScanny.Scanning.Ntfs;
using PuterScanny.Signals.Usage;

namespace PuterScanny.Tests;

public sealed class MftParserTests
{
    private const int RecordSize = 1024, Sector = 512;

    /// <summary>Builds a FILE record the way NTFS stores it on disk (with update-sequence fix-ups applied).</summary>
    private static byte[] BuildRecord(Action<List<byte[]>> addAttributes, ushort flags = 0x01, ulong baseRecord = 0)
    {
        var r = new byte[RecordSize];
        "FILE"u8.CopyTo(r);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 0x30);   // update sequence array offset
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 3);      // 1 + one entry per 512-byte sector
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), 0x38); // first attribute
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x16), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0x20), baseRecord);

        var attrs = new List<byte[]>();
        addAttributes(attrs);
        int pos = 0x38;
        foreach (var a in attrs) { a.CopyTo(r, pos); pos += a.Length; }
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(pos), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x18), (uint)(pos + 8));

        // Fix-ups: save each sector's last two bytes in the array and stamp the check value there.
        ushort usn = 0x1234;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x30), usn);
        for (int i = 1; i <= 2; i++)
        {
            int end = i * Sector - 2;
            r.AsSpan(end, 2).CopyTo(r.AsSpan(0x30 + i * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), usn);
        }
        return r;
    }

    private static byte[] Resident(uint type, byte[] value, string name = "")
    {
        int nameOffset = 24;
        int valueOffset = (nameOffset + name.Length * 2 + 7) & ~7;
        int length = (valueOffset + value.Length + 7) & ~7;
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[9] = (byte)name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), (ushort)nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(20), (ushort)valueOffset);
        Encoding.Unicode.GetBytes(name).CopyTo(a, nameOffset);
        value.CopyTo(a, valueOffset);
        return a;
    }

    private static byte[] NonResidentData(long allocated, long real, byte[] runs)
    {
        int length = (72 + runs.Length + 7) & ~7;
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, MftRecordParser.AttrData);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 1; // non-resident
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(32), 72);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(40), allocated);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(48), real);
        runs.CopyTo(a, 72);
        return a;
    }

    private static byte[] StandardInfo(DateTime created, DateTime modified, uint attributes)
    {
        var v = new byte[72];
        BinaryPrimitives.WriteInt64LittleEndian(v, created.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), modified.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(24), modified.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(32), attributes);
        return v;
    }

    private static byte[] FileName(ulong parent, string name, byte ns)
    {
        var v = new byte[66 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, parent | (1UL << 48)); // sequence number in the top bits must be ignored
        v[64] = (byte)name.Length;
        v[65] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 66);
        return v;
    }

    [Fact]
    public void A_file_record_is_read_completely()
    {
        var created = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var modified = new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        byte[] record = BuildRecord(a =>
        {
            a.Add(Resident(MftRecordParser.AttrStandardInformation, StandardInfo(created, modified, 0x20)));
            a.Add(Resident(MftRecordParser.AttrFileName, FileName(42, "REPORT~1.PDF", 2)));     // old 8.3 name
            a.Add(Resident(MftRecordParser.AttrFileName, FileName(42, "Quarterly report.pdf", 1)));
            a.Add(NonResidentData(allocated: 8192, real: 5000, runs: [0x11, 0x02, 0x40, 0x00]));
            a.Add(Resident(MftRecordParser.AttrData, "[ZoneTransfer]\r\nZoneId=3\r\n"u8.ToArray(), "Zone.Identifier"));
        });

        Assert.True(MftRecordParser.ApplyFixups(record, Sector));
        var info = MftRecordParser.Parse(record, 77)!;
        Assert.True(info.InUse);
        Assert.False(info.IsDirectory);
        Assert.Equal(created.ToFileTimeUtc(), info.Created);
        Assert.Equal(modified.ToFileTimeUtc(), info.Modified);
        Assert.Equal(0x20u, info.Attributes);
        Assert.Equal(2, info.Names.Count);
        Assert.Contains(info.Names, n => n.Name == "Quarterly report.pdf" && n.ParentRecord == 42 && n.Namespace == 1);
        Assert.Equal(5000, info.Size);
        Assert.Equal(8192, info.Allocated);
        var stream = Assert.Single(info.Streams);
        Assert.Equal("Zone.Identifier", stream.Name);
        Assert.Equal(26, stream.Size);
    }

    [Fact]
    public void Torn_records_and_free_records_are_recognised()
    {
        byte[] torn = BuildRecord(a => a.Add(Resident(MftRecordParser.AttrFileName, FileName(5, "x.txt", 1))));
        BinaryPrimitives.WriteUInt16LittleEndian(torn.AsSpan(Sector - 2), 0xBEEF); // a sector from a different write
        Assert.False(MftRecordParser.ApplyFixups(torn, Sector));

        byte[] free = BuildRecord(_ => { }, flags: 0x00);
        Assert.True(MftRecordParser.ApplyFixups(free, Sector));
        Assert.False(MftRecordParser.Parse(free, 9)!.InUse);

        byte[] ext = BuildRecord(a => a.Add(NonResidentData(4096, 4000, [0x11, 0x01, 0x10, 0x00])), baseRecord: 300 | (5UL << 48));
        Assert.True(MftRecordParser.ApplyFixups(ext, Sector));
        Assert.Equal(300UL, MftRecordParser.Parse(ext, 1000)!.BaseRecord);

        Assert.Null(MftRecordParser.Parse(new byte[RecordSize], 1));
    }

    [Fact]
    public void Sparse_space_is_not_counted_as_used()
    {
        // Like NTFS's $BadClus:$Bad: "allocated" says 930 GB, but it's one sparse run (nothing stored).
        byte[] bad = BuildRecord(a => a.Add(NonResidentData(allocated: 930L << 30, real: 930L << 30, runs: [0x04, 0x00, 0x00, 0x00, 0x0F, 0x00])));
        Assert.True(MftRecordParser.ApplyFixups(bad, Sector));
        var info = MftRecordParser.Parse(bad, 8)!;
        Assert.Equal(930L << 30, info.Size);
        Assert.Equal(0, info.DataClusters);

        // A sparse file with two real pieces around a hole: only the stored clusters count.
        byte[] sparse = BuildRecord(a => a.Add(NonResidentData(allocated: 40960, real: 40960, runs: [0x11, 0x02, 0x10, 0x01, 0x05, 0x11, 0x03, 0x20, 0x00])));
        Assert.True(MftRecordParser.ApplyFixups(sparse, Sector));
        Assert.Equal(5, MftRecordParser.Parse(sparse, 9)!.DataClusters);
    }

    [Fact]
    public void Data_runs_decode_to_absolute_clusters()
    {
        // 16 clusters at 4096; 5 clusters one cluster back (4095); then 8 sparse clusters.
        byte[] runs = [0x31, 0x10, 0x00, 0x10, 0x00, 0x11, 0x05, 0xFF, 0x01, 0x08, 0x00];
        var r = MftRecordParser.DecodeRuns(runs);
        Assert.Equal([(4096L, 16L), (4095L, 5L), (-1L, 8L)], r);
    }
}

public sealed class AdminUsageParsingTests
{
    private static byte[] Scca(string exe, string volumePath, int runCount, DateTime[] runs)
    {
        var d = new byte[0x400];
        BinaryPrimitives.WriteInt32LittleEndian(d, 30);
        "SCCA"u8.CopyTo(d.AsSpan(4));
        Encoding.Unicode.GetBytes(exe).CopyTo(d, 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x54), 0x130);
        byte[] names = Encoding.Unicode.GetBytes(@"\VOLUME{01d00000000000aa-ABCD1234}\WINDOWS\SYSTEM32\NTDLL.DLL" + "\0" + volumePath + "\0");
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x64), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x68), names.Length);
        names.CopyTo(d, 0x200);
        for (int i = 0; i < runs.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0x80 + i * 8), runs[i].ToFileTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0xD0), runCount);
        return d;
    }

    [Fact]
    public void Compressed_prefetch_files_are_decoded_and_parsed()
    {
        var newest = new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc);
        byte[] scca = Scca("NOTEPAD.EXE", @"\VOLUME{01d00000000000aa-ABCD1234}\WINDOWS\SYSTEM32\NOTEPAD.EXE", 7, [newest, newest.AddDays(-3)]);
        byte[] onDisk = PrefetchReader.CompressForTest(scca);
        Assert.Equal((byte)'M', onDisk[0]);

        byte[]? plain = PrefetchReader.Decompress(onDisk);
        Assert.NotNull(plain);
        Assert.Equal(scca, plain);

        var entry = PrefetchReader.Parse(plain!, new Dictionary<string, string> { ["ABCD1234"] = "C:" })!;
        Assert.Equal("NOTEPAD.EXE", entry.ExeName);
        Assert.Equal(@"C:\WINDOWS\SYSTEM32\NOTEPAD.EXE", entry.FullPath);
        Assert.Equal(7, entry.RunCount);
        Assert.Equal(newest, entry.RunTimesUtc[0]);
        Assert.Equal(2, entry.RunTimesUtc.Count);

        // A drive that isn't connected any more: the program is still listed, just without a path.
        Assert.Null(PrefetchReader.Parse(plain!, new Dictionary<string, string>())!.FullPath);
        Assert.Null(PrefetchReader.Decompress("not a prefetch file"u8.ToArray()));
    }

    [Fact]
    public void Background_activity_paths_map_to_drive_letters()
    {
        var devices = new Dictionary<string, string> { [@"\Device\HarddiskVolume3"] = "C:", [@"\Device\HarddiskVolume7"] = "D:" };
        Assert.Equal(@"C:\Program Files\App\app.exe", BamReader.ToDrivePath(@"\Device\HarddiskVolume3\Program Files\App\app.exe", devices));
        Assert.Equal(@"D:\Games\x.exe", BamReader.ToDrivePath(@"\Device\HarddiskVolume7\Games\x.exe", devices));
        Assert.Null(BamReader.ToDrivePath(@"\Device\HarddiskVolume30\x.exe", devices)); // not HarddiskVolume3!
        Assert.Contains(BamReader.DeviceToDrive(), kv => kv.Value == "C:" && kv.Key.StartsWith(@"\Device\", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_administrator_rights_the_admin_records_are_reported_not_checked()
    {
        if (PuterScanny.Scanning.ElevationInfo.IsProcessElevated()) return;
        var usage = UsageCollector.Collect([], null, default);
        Assert.Contains(usage.Sources, s => s.Source == PuterScanny.Core.Models.UsageSource.Prefetch && !s.Available && s.Note.Contains("administrator"));
        Assert.Contains(usage.Sources, s => s.Source == PuterScanny.Core.Models.UsageSource.Amcache && !s.Available);
    }

    [Fact]
    public void Deep_scan_without_administrator_rights_falls_back_and_says_why()
    {
        if (PuterScanny.Scanning.ElevationInfo.IsProcessElevated()) return;
        using var tree = new TempTree();
        tree.File("a.txt", 10);
        var sink = new ListSink();
        new PuterScanny.Scanning.FileSystemScanner().Scan(new PuterScanny.Core.Models.ScanOptions { RootPath = tree.Root, Mode = PuterScanny.Core.Models.ScanMode.Deep }, sink);
        Assert.Equal(1, sink.Final!.FileCount);
        Assert.Contains("administrator", sink.Final.Method);
    }
}
