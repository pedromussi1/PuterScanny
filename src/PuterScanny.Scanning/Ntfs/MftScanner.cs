using System.Diagnostics;
using PuterScanny.Core.Abstractions;
using PuterScanny.Core.IO;
using PuterScanny.Core.Models;

namespace PuterScanny.Scanning.Ntfs;

/// <summary>
/// Deep scan: builds the complete folder tree from the drive's master file table and streams it into the same
/// <see cref="IScanSink"/> as the normal scanner, so everything downstream works unchanged. Differences from the
/// normal listing: sizes on disk are exact, hard links are counted once, NTFS's own files ($MFT, $LogFile…) and
/// folders even administrators can't list (System Volume Information) are included.
/// </summary>
internal sealed class MftScanner
{
    private const ulong RootRecord = 5;
    private const ulong ExtendRecord = 11;
    private const uint NameSurrogate = 0x20000000;

    private readonly MftVolume _volume;
    private readonly string _root;
    private readonly ScanOptions _options;
    private readonly IScanSink _sink;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly CancellationToken _ct;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Per-record facts, indexed by record number.
    private ulong[] _parent = [];
    private string?[] _name = [];
    private bool[] _isDir = [];
    private uint[] _attrs = [];
    private long[] _created = [], _modified = [], _accessed = [], _size = [], _alloc = [], _clusters = [], _streamClusters = [];
    private uint[] _tag = [];
    private readonly Dictionary<ulong, List<MftStream>> _streams = new();
    private readonly Dictionary<ulong, List<(ulong Record, string Name)>> _extraLinks = new();   // parent → hard-link children
    private readonly Dictionary<ulong, List<ulong>> _children = new();

    private long _nextId, _files, _dirs, _bytes, _bytesOnDisk, _issues, _streamCount, _streamBytes;
    private TimeSpan _lastReport = TimeSpan.MinValue;

    private sealed class DirAgg(long? parentId)
    {
        public readonly long? ParentId = parentId;
        public long Size, SizeOnDisk, Files, Dirs;
    }

    private readonly Dictionary<long, DirAgg> _agg = new();

    /// <summary>Files whose space is already counted (a file with several hard links is counted at the first name met).</summary>
    private readonly HashSet<ulong> _counted = new();

    public MftScanner(MftVolume volume, string root, ScanOptions options, IScanSink sink, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        _volume = volume;
        _root = root;
        _options = options;
        _sink = sink;
        _progress = progress;
        _ct = ct;
    }

    public ScanSummary Execute(ScanSummary initial)
    {
        ScanStatus status = ScanStatus.Completed;
        string? error = null;
        try
        {
            LoadTable();
            ulong rootRecord = string.Equals(Path.GetPathRoot(_root), _root, StringComparison.OrdinalIgnoreCase)
                ? RootRecord
                : MftVolume.RecordNumberOf(_root) ?? throw new IOException("The scanned folder couldn't be found in the drive's file table.");
            Walk(rootRecord);
        }
        catch (OperationCanceledException)
        {
            status = ScanStatus.Cancelled;
        }

        Report(ScanPhase.ComputingTotals, force: true);
        var totals = ComputeTotals();
        _sink.SetDirectoryTotals(totals);
        var rootAgg = _agg.GetValueOrDefault(1) ?? new DirAgg(null);
        var final = initial with
        {
            CompletedUtc = DateTime.UtcNow,
            Status = status,
            ErrorMessage = error,
            FileCount = rootAgg.Files,
            DirectoryCount = rootAgg.Dirs,
            TotalSize = rootAgg.Size,
            TotalSizeOnDisk = rootAgg.SizeOnDisk,
            IssueCount = _issues,
            StreamCount = _streamCount,
            StreamBytes = _streamBytes,
        };
        Report(ScanPhase.Finalizing, force: true);
        _sink.Complete(final);
        Report(ScanPhase.Done, force: true);
        return final;
    }

    private void LoadTable()
    {
        long n = _volume.RecordCount;
        _parent = new ulong[n]; _name = new string?[n]; _isDir = new bool[n]; _attrs = new uint[n];
        _created = new long[n]; _modified = new long[n]; _accessed = new long[n]; _size = new long[n]; _alloc = new long[n]; _tag = new uint[n];
        _clusters = new long[n]; _streamClusters = new long[n];
        var allNames = new Dictionary<ulong, List<MftName>>();

        foreach (var rec in _volume.ReadAll(_ct, (done, total) => ReportReading(done, total)))
        {
            ulong id = rec.BaseRecord != 0 ? rec.BaseRecord : rec.RecordNumber;
            if (id >= (ulong)n) continue;
            if (rec.BaseRecord == 0)
            {
                _isDir[id] = rec.IsDirectory;
                if (rec.HasStandardInfo)
                {
                    _attrs[id] = rec.Attributes;
                    _created[id] = rec.Created;
                    _modified[id] = rec.Modified;
                    _accessed[id] = rec.Accessed;
                }
            }
            if (rec.Names.Count > 0)
            {
                if (!allNames.TryGetValue(id, out var list)) allNames[id] = list = [];
                list.AddRange(rec.Names);
            }
            if (rec.Size >= 0) { _size[id] = rec.Size; _alloc[id] = rec.Allocated; }
            _clusters[id] += rec.DataClusters;
            if (rec.StreamClusters is { } sc) foreach (var c in sc.Values) _streamClusters[id] += c;
            if (rec.Streams.Count > 0)
            {
                if (!_streams.TryGetValue(id, out var s)) _streams[id] = s = [];
                s.AddRange(rec.Streams);
            }
            if (rec.ReparseTag != 0) _tag[id] = rec.ReparseTag;
        }

        // Pick each item's main name (not the old 8.3 "DOS" name) and remember any extra hard links.
        foreach (var (id, names) in allNames)
        {
            var real = names.Where(x => x.Namespace != 2).ToList();
            if (real.Count == 0) real = names;
            var main = real.FirstOrDefault(x => x.Namespace is 1 or 3);
            if (main.Name is null) main = real[0];
            _name[id] = main.Name;
            _parent[id] = main.ParentRecord;
            if (id != RootRecord) AddChild(main.ParentRecord, id);

            foreach (var link in real.Where(x => x.ParentRecord != main.ParentRecord || !string.Equals(x.Name, main.Name, StringComparison.OrdinalIgnoreCase))
                                     .DistinctBy(x => (x.ParentRecord, x.Name.ToUpperInvariant())))
            {
                if (!_extraLinks.TryGetValue(link.ParentRecord, out var l)) _extraLinks[link.ParentRecord] = l = [];
                l.Add((id, link.Name));
            }
        }
    }

    private void AddChild(ulong parent, ulong child)
    {
        if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = [];
        list.Add(child);
    }

    private readonly record struct Pending(ulong Record, long Id, string Path, int Depth);

    private void Walk(ulong rootRecord)
    {
        long rootId = ++_nextId;
        string rootName = Path.GetFileName(_root.TrimEnd('\\')) is { Length: > 0 } nm ? nm : _root;
        _sink.AddEntry(new FileSystemEntry
        {
            Id = rootId, ParentId = null, Name = rootName, Path = _root, IsDirectory = true, Depth = 0,
            Attributes = (FileAttributes)_attrs[rootRecord], CreatedUtc = Time(_created[rootRecord]), ModifiedUtc = Time(_modified[rootRecord]),
            AccessedUtc = Time(_accessed[rootRecord]),
        });
        _agg[rootId] = new DirAgg(null);

        var stack = new Stack<Pending>();
        stack.Push(new Pending(rootRecord, rootId, _root, 0));
        var visited = new HashSet<ulong> { rootRecord };
        int processed = 0;

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var kids = new List<(ulong Record, string Name, bool Secondary)>();
            if (_children.TryGetValue(dir.Record, out var children))
                foreach (var c in children) if (c != dir.Record && _name[c] is { } n) kids.Add((c, n, false));
            if (_extraLinks.TryGetValue(dir.Record, out var links))
                foreach (var (rec, n) in links) kids.Add((rec, n, true));

            foreach (var (rec, name, secondary) in kids)
            {
                if ((++processed & 1023) == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    Report(ScanPhase.Enumerating, dir.Path);
                }
                Emit(dir, rec, name, secondary, stack, visited);
            }
        }
    }

    private void Emit(Pending parent, ulong rec, string name, bool secondaryLink, Stack<Pending> stack, HashSet<ulong> visited)
    {
        string path = LongPath.Combine(parent.Path, name);
        long id = ++_nextId;
        uint attrs = _attrs[rec];
        uint tag = _tag[rec];
        var flags = EntryFlags.None;
        if ((attrs & 0x800) != 0) flags |= EntryFlags.Compressed;
        if ((attrs & 0x200) != 0) flags |= EntryFlags.Sparse;
        if ((attrs & (0x400000 | 0x40000 | 0x1000)) != 0) flags |= EntryFlags.CloudPlaceholder;

        bool system = rec < 24 || _parent[rec] == ExtendRecord;
        var streams = system ? null : _streams.GetValueOrDefault(rec);
        long streamBytes = streams?.Sum(s => s.Size) ?? 0;
        if (streams is { Count: > 0 }) flags |= EntryFlags.HasAlternateStreams;

        if (_isDir[rec])
        {
            bool isLink = (attrs & 0x400) != 0 && (tag & NameSurrogate) != 0;
            if (isLink)
            {
                flags |= EntryFlags.ReparsePointNotFollowed;
                _issues++;
                _sink.AddIssue(new ScanIssue(path, ScanIssueKind.LinkNotFollowed, 0, $"{ReparseTags.Describe(tag)} not followed, so nothing is counted twice."));
            }
            _sink.AddEntry(new FileSystemEntry
            {
                Id = id, ParentId = parent.Id, Name = name, Path = path, IsDirectory = true, Depth = parent.Depth + 1,
                Attributes = (FileAttributes)attrs, CreatedUtc = Time(_created[rec]), ModifiedUtc = Time(_modified[rec]), AccessedUtc = Time(_accessed[rec]),
                ReparseTag = tag, Flags = flags, StreamBytes = streamBytes,
            });
            _agg[id] = new DirAgg(parent.Id);
            _agg[parent.Id].Dirs++;
            _dirs++;
            if (!isLink && !secondaryLink && visited.Add(rec)) stack.Push(new Pending(rec, id, path, parent.Depth + 1));
        }
        else
        {
            long size = _size[rec];
            long alloc = _clusters[rec] * _volume.ClusterSize; // clusters really stored (0 for tiny files kept inside the table)
            if (system)
            {
                // NTFS's own files: count the space they really occupy, streams included (some, like $BadClus or
                // $UsnJrnl, report huge sizes that are almost entirely empty placeholders).
                alloc += _streamClusters[rec] * _volume.ClusterSize;
                size = alloc;
            }
            secondaryLink = !_counted.Add(rec);
            if (secondaryLink) flags |= EntryFlags.HardLinkSecondary;

            _sink.AddEntry(new FileSystemEntry
            {
                Id = id, ParentId = parent.Id, Name = name, Path = path, IsDirectory = false, Depth = parent.Depth + 1,
                Size = Math.Max(0, size), SizeOnDisk = secondaryLink ? 0 : alloc,
                Attributes = (FileAttributes)attrs, CreatedUtc = Time(_created[rec]), ModifiedUtc = Time(_modified[rec]), AccessedUtc = Time(_accessed[rec]),
                ReparseTag = tag, Flags = flags, StreamBytes = streamBytes,
                TotalSize = secondaryLink ? 0 : Math.Max(0, size), TotalSizeOnDisk = secondaryLink ? 0 : alloc,
            });

            // A hard link is the same data under another name: count its space once.
            if (!secondaryLink)
            {
                var agg = _agg[parent.Id];
                agg.Size += Math.Max(0, size);
                agg.SizeOnDisk += alloc;
                _bytes += Math.Max(0, size);
                _bytesOnDisk += alloc;
            }
            _agg[parent.Id].Files++;
            _files++;
        }

        if (streams is not null && _options.IncludeAlternateStreams)
        {
            foreach (var s in streams)
            {
                _sink.AddStream(new AlternateStream(id, s.Name, s.Size));
                _streamCount++;
                _streamBytes += s.Size;
            }
        }
    }

    private List<DirectoryTotals> ComputeTotals()
    {
        var ids = _agg.Keys.OrderByDescending(k => k).ToList();
        var result = new List<DirectoryTotals>(ids.Count);
        foreach (long id in ids)
        {
            var a = _agg[id];
            if (a.ParentId is long p && _agg.TryGetValue(p, out var parent))
            {
                parent.Size += a.Size;
                parent.SizeOnDisk += a.SizeOnDisk;
                parent.Files += a.Files;
                parent.Dirs += a.Dirs;
            }
            result.Add(new DirectoryTotals(id, a.Size, a.SizeOnDisk, a.Files, a.Dirs, EntryFlags.None));
        }
        return result;
    }

    private static DateTime Time(long fileTime)
    {
        if (fileTime <= 0) return default;
        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return default; }
    }

    private void ReportReading(long done, long total)
    {
        if (_progress is null) return;
        var now = _clock.Elapsed;
        if (now - _lastReport < TimeSpan.FromMilliseconds(200)) return;
        _lastReport = now;
        _progress.Report(new ScanProgress(ScanPhase.Enumerating, 0, 0, 0, 0, 0,
            $"Reading the drive's file table directly: {done:N0} of {total:N0} records", now, total > 0 ? 0.5 * done / total : null));
    }

    private void Report(ScanPhase phase, string? current = null, bool force = false)
    {
        if (_progress is null) return;
        var now = _clock.Elapsed;
        if (!force && now - _lastReport < TimeSpan.FromMilliseconds(200)) return;
        _lastReport = now;
        double? fraction = phase == ScanPhase.Enumerating ? 0.5 + 0.5 * Math.Min(1, (double)(_files + _dirs) / Math.Max(1, _volume.RecordCount)) : null;
        _progress.Report(new ScanProgress(phase, _files, _dirs, _bytes, _bytesOnDisk, _issues, current, now, fraction));
    }
}
