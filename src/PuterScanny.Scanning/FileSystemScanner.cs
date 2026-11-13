using System.Diagnostics;
using System.Runtime.InteropServices;
using PuterScanny.Core.Abstractions;
using PuterScanny.Core.IO;
using PuterScanny.Core.Models;
using static PuterScanny.Scanning.Native.NativeMethods;

namespace PuterScanny.Scanning;

/// <summary>
/// Walks every file and folder under a root using the Win32 Find APIs (fast, long-path safe), and streams
/// everything it finds into an <see cref="IScanSink"/>. Read-only: it never modifies, moves or downloads anything.
/// </summary>
public sealed class FileSystemScanner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public ScanSummary Scan(ScanOptions options, IScanSink sink, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        string root = LongPath.NormalizeRoot(options.RootPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The folder \"{root}\" does not exist or cannot be reached.");

        // Deep scan: read the drive's file table directly when Windows allows it; otherwise list folders as usual.
        string? deepProblem = null;
        if (options.Mode == ScanMode.Deep)
        {
            if (!ElevationInfo.IsProcessElevated()) deepProblem = "Deep scan needs administrator rights.";
            else if (Ntfs.MftVolume.TryOpen(root, out deepProblem) is { } volume)
            {
                using (volume)
                {
                    var initial = new ScanSummary
                    {
                        RootPath = root, Mode = options.Mode, IsElevated = true, StartedUtc = DateTime.UtcNow, Status = ScanStatus.Running,
                        ClusterSize = volume.ClusterSize, MachineName = Environment.MachineName,
                        AppVersion = typeof(FileSystemScanner).Assembly.GetName().Version?.ToString(),
                        Method = "Read the drive's file table directly (Deep scan): exact sizes, hard links counted once, nothing hidden.",
                    };
                    sink.Begin(initial);
                    return new Ntfs.MftScanner(volume, root, options, sink, progress, cancellationToken).Execute(initial);
                }
            }
        }

        var run = new ScanRun(root, options, sink, progress, cancellationToken, deepProblem);
        return run.Execute();
    }

    private sealed class DirAgg(long? parentId)
    {
        public readonly long? ParentId = parentId;
        public long Size;
        public long SizeOnDisk;
        public long Files;
        public long Dirs;
        public EntryFlags Flags;
    }

    private readonly record struct PendingDir(long Id, string Path, int Depth);

    private sealed class ScanRun
    {
        private readonly string _root;
        private readonly ScanOptions _options;
        private readonly IScanSink _sink;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly Stopwatch _clock = new();
        private readonly Dictionary<long, DirAgg> _dirs = new();
        private readonly Stack<PendingDir> _stack = new();
        private readonly long _clusterSize;
        private readonly long? _volumeUsedBytes;
        private readonly bool _streamsSupported;

        private long _nextId;
        private long _files, _dirCount, _bytes, _bytesOnDisk, _issues, _streamCount, _streamBytes;
        private TimeSpan _lastReport = TimeSpan.MinValue;
        private string? _currentPath;

        private readonly string? _deepProblem;

        public ScanRun(string root, ScanOptions options, IScanSink sink, IProgress<ScanProgress>? progress, CancellationToken ct, string? deepProblem = null)
        {
            _deepProblem = deepProblem;
            _root = root;
            _options = options;
            _sink = sink;
            _progress = progress;
            _ct = ct;
            _clusterSize = GetClusterSize(root);

            string? volumeRoot = Path.GetPathRoot(root);
            try
            {
                var drive = volumeRoot is null ? null : new DriveInfo(volumeRoot);
                if (drive is { IsReady: true })
                {
                    // Only a whole-drive scan can be measured against the drive's used space.
                    if (string.Equals(volumeRoot, root, StringComparison.OrdinalIgnoreCase))
                        _volumeUsedBytes = drive.TotalSize - drive.TotalFreeSpace;
                    _streamsSupported = drive.DriveFormat is "NTFS" or "ReFS";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _streamsSupported = false;
            }
        }

        public ScanSummary Execute()
        {
            _clock.Start();
            var started = DateTime.UtcNow;
            bool elevated = ElevationInfo.IsProcessElevated();

            var initial = new ScanSummary
            {
                RootPath = _root,
                Mode = _options.Mode,
                IsElevated = elevated,
                StartedUtc = started,
                Status = ScanStatus.Running,
                ClusterSize = _clusterSize,
                MachineName = Environment.MachineName,
                AppVersion = typeof(FileSystemScanner).Assembly.GetName().Version?.ToString(),
                Method = _deepProblem is null
                    ? (elevated ? "Listed every folder (as administrator)." : "Listed every folder (standard scan).")
                    : $"Listed every folder: the drive's file table couldn't be read directly ({_deepProblem})",
            };
            _sink.Begin(initial);
            Report(ScanPhase.Preparing, force: true);

            ScanStatus status = ScanStatus.Completed;
            string? error = null;
            try
            {
                AddRoot();
                while (_stack.Count > 0)
                {
                    if (_ct.IsCancellationRequested)
                    {
                        status = ScanStatus.Cancelled;
                        break;
                    }
                    EnumerateDirectory(_stack.Pop());
                }
            }
            catch (OperationCanceledException)
            {
                status = ScanStatus.Cancelled;
            }
            catch (Exception ex)
            {
                status = ScanStatus.Failed;
                error = ex.Message;
            }

            Report(ScanPhase.ComputingTotals, force: true);
            var totals = ComputeTotals();
            _sink.SetDirectoryTotals(totals);

            DirAgg rootAgg = _dirs[1];
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

        private void AddRoot()
        {
            var info = new DirectoryInfo(_root);
            FileAttributes attrs = FileAttributes.Directory;
            DateTime created = default, modified = default, accessed = default;
            try
            {
                attrs = info.Attributes;
                created = info.CreationTimeUtc;
                modified = info.LastWriteTimeUtc;
                accessed = info.LastAccessTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Root metadata is cosmetic; listing errors are reported when the folder is enumerated.
            }

            long id = ++_nextId;
            string name = Path.GetFileName(_root.TrimEnd('\\')) is { Length: > 0 } n ? n : _root;
            _sink.AddEntry(new FileSystemEntry
            {
                Id = id,
                ParentId = null,
                Name = name,
                Path = _root,
                IsDirectory = true,
                Depth = 0,
                Attributes = attrs,
                CreatedUtc = created,
                ModifiedUtc = modified,
                AccessedUtc = accessed,
            });
            _dirs[id] = new DirAgg(null);
            _stack.Push(new PendingDir(id, _root, 0));
        }

        private void EnumerateDirectory(PendingDir dir)
        {
            _currentPath = dir.Path;
            string pattern = LongPath.Combine(LongPath.ToNative(dir.Path), "*");

            using SafeFindHandle handle = FindFirstFileEx(
                pattern, FINDEX_INFO_LEVELS.FindExInfoBasic, out WIN32_FIND_DATA data,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err is ERROR_FILE_NOT_FOUND or ERROR_NO_MORE_FILES) return; // empty folder
                _dirs[dir.Id].Flags |= EntryFlags.EnumerationFailed;
                AddIssue(dir.Path, err, isListing: true);
                return;
            }

            int processed = 0;
            do
            {
                string name = data.cFileName;
                if (name is "." or "..") continue;

                ProcessChild(dir, in data);

                if (++processed % 256 == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    Report(ScanPhase.Enumerating);
                }
            }
            while (FindNextFile(handle, out data));

            int last = Marshal.GetLastPInvokeError();
            if (last != ERROR_NO_MORE_FILES)
            {
                _dirs[dir.Id].Flags |= EntryFlags.EnumerationFailed;
                AddIssue(dir.Path, last, isListing: true, partial: true);
            }

            Report(ScanPhase.Enumerating);
        }

        private void ProcessChild(PendingDir parent, in WIN32_FIND_DATA data)
        {
            uint rawAttrs = data.dwFileAttributes;
            var attrs = (FileAttributes)rawAttrs;
            bool isDir = attrs.HasFlag(FileAttributes.Directory);
            bool isReparse = attrs.HasFlag(FileAttributes.ReparsePoint);
            uint tag = isReparse ? data.dwReserved0 : 0;
            string path = LongPath.Combine(parent.Path, data.cFileName);
            long id = ++_nextId;
            int depth = parent.Depth + 1;
            var flags = EntryFlags.None;
            if (attrs.HasFlag(FileAttributes.Compressed)) flags |= EntryFlags.Compressed;
            if (attrs.HasFlag(FileAttributes.SparseFile)) flags |= EntryFlags.Sparse;

            // Opening a "recall on open" or offline file can make the cloud provider download it, so never touch those.
            bool mustNotOpen = (rawAttrs & FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0 || attrs.HasFlag(FileAttributes.Offline);
            bool dataNotLocal = mustNotOpen || (rawAttrs & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0;

            List<(string Name, long Size)>? streams = null;
            if (_options.IncludeAlternateStreams && _streamsSupported && !mustNotOpen && !(isDir && isReparse && !ReparseTags.ShouldDescend(tag)))
                streams = EnumerateStreams(path);

            long streamBytes = 0;
            if (streams is { Count: > 0 })
            {
                flags |= EntryFlags.HasAlternateStreams;
                foreach (var s in streams) streamBytes += s.Size;
            }

            FileSystemEntry entry;
            if (isDir)
            {
                bool descend = !isReparse || ReparseTags.ShouldDescend(tag);
                string? target = null;
                if (!descend)
                {
                    flags |= EntryFlags.ReparsePointNotFollowed;
                    target = TryGetLinkTarget(path);
                    RecordIssue(new ScanIssue(path, ScanIssueKind.LinkNotFollowed, 0,
                        $"{ReparseTags.Describe(tag)} not followed{(target is null ? "" : $" (points to {target})")}, so nothing is counted twice."));
                }

                entry = new FileSystemEntry
                {
                    Id = id,
                    ParentId = parent.Id,
                    Name = data.cFileName,
                    Path = path,
                    IsDirectory = true,
                    Depth = depth,
                    Attributes = attrs,
                    CreatedUtc = data.ftCreationTime.ToUtc(),
                    ModifiedUtc = data.ftLastWriteTime.ToUtc(),
                    AccessedUtc = data.ftLastAccessTime.ToUtc(),
                    ReparseTag = tag,
                    Flags = flags,
                    StreamBytes = streamBytes,
                    LinkTarget = target,
                };

                _dirs[id] = new DirAgg(parent.Id) { Flags = flags };
                _dirs[parent.Id].Dirs++;
                _dirCount++;
                _sink.AddEntry(entry);
                if (descend) _stack.Push(new PendingDir(id, path, depth));
            }
            else
            {
                long size = data.FileSize;
                long onDisk;
                if (dataNotLocal)
                {
                    flags |= EntryFlags.CloudPlaceholder;
                    onDisk = mustNotOpen ? 0 : TryGetAllocatedSize(path) ?? 0;
                }
                else if ((flags & (EntryFlags.Compressed | EntryFlags.Sparse)) != 0)
                {
                    onDisk = TryGetAllocatedSize(path) ?? RoundToCluster(size);
                }
                else
                {
                    onDisk = RoundToCluster(size);
                    flags |= EntryFlags.SizeOnDiskEstimated;
                }

                string? target = null;
                if (isReparse && !ReparseTags.ShouldDescend(tag))
                {
                    flags |= EntryFlags.ReparsePointNotFollowed;
                    target = TryGetLinkTarget(path, isFile: true);
                }

                entry = new FileSystemEntry
                {
                    Id = id,
                    ParentId = parent.Id,
                    Name = data.cFileName,
                    Path = path,
                    IsDirectory = false,
                    Depth = depth,
                    Size = size,
                    SizeOnDisk = onDisk,
                    Attributes = attrs,
                    CreatedUtc = data.ftCreationTime.ToUtc(),
                    ModifiedUtc = data.ftLastWriteTime.ToUtc(),
                    AccessedUtc = data.ftLastAccessTime.ToUtc(),
                    ReparseTag = tag,
                    Flags = flags,
                    StreamBytes = streamBytes,
                    LinkTarget = target,
                    TotalSize = size,
                    TotalSizeOnDisk = onDisk,
                };

                DirAgg agg = _dirs[parent.Id];
                agg.Size += size;
                agg.SizeOnDisk += onDisk;
                agg.Files++;
                _files++;
                _bytes += size;
                _bytesOnDisk += onDisk;
                _sink.AddEntry(entry);
            }

            if (streams is not null)
            {
                foreach (var s in streams)
                {
                    _sink.AddStream(new AlternateStream(id, s.Name, s.Size));
                    _streamCount++;
                    _streamBytes += s.Size;
                }
            }
        }

        private List<(string Name, long Size)>? EnumerateStreams(string path)
        {
            using SafeFindHandle handle = FindFirstStream(LongPath.ToNative(path), 0, out WIN32_FIND_STREAM_DATA sd, 0);
            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastPInvokeError();
                // EOF = no data streams at all (normal for folders). Access/sharing problems are expected for system
                // files and are already visible through the folder listing, so only unusual failures become issues.
                if (err is not (ERROR_HANDLE_EOF or ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION or ERROR_FILE_NOT_FOUND
                    or ERROR_PATH_NOT_FOUND or ERROR_INVALID_PARAMETER or ERROR_CANT_ACCESS_FILE))
                {
                    RecordIssue(new ScanIssue(path, ScanIssueKind.StreamEnumerationError, err, $"Hidden data streams could not be listed: {Win32Message(err)}"));
                }
                return null;
            }

            List<(string, long)>? result = null;
            do
            {
                // Names look like ":Zone.Identifier:$DATA"; the main content is "::$DATA" and is skipped.
                string raw = sd.cStreamName;
                if (raw == "::$DATA") continue;
                string name = raw.StartsWith(':') ? raw[1..] : raw;
                if (name.EndsWith(":$DATA", StringComparison.Ordinal)) name = name[..^6];
                (result ??= []).Add((name, sd.StreamSize));
            }
            while (FindNextStream(handle, out sd));

            return result;
        }

        private static long? TryGetAllocatedSize(string path)
        {
            uint low = GetCompressedFileSize(LongPath.ToNative(path), out uint high);
            if (low == INVALID_FILE_SIZE && Marshal.GetLastPInvokeError() != 0) return null;
            return ((long)high << 32) | low;
        }

        private static string? TryGetLinkTarget(string path, bool isFile = false)
        {
            try
            {
                FileSystemInfo info = isFile ? new FileInfo(path) : new DirectoryInfo(path);
                return info.LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private long RoundToCluster(long size)
        {
            if (size <= 0 || _clusterSize <= 0) return size;
            return (size + _clusterSize - 1) / _clusterSize * _clusterSize;
        }

        private void AddIssue(string path, int err, bool isListing, bool partial = false)
        {
            var kind = err switch
            {
                ERROR_ACCESS_DENIED => ScanIssueKind.AccessDenied,
                ERROR_SHARING_VIOLATION => ScanIssueKind.SharingViolation,
                _ => ScanIssueKind.EnumerationError,
            };
            string what = isListing
                ? partial ? "Folder listing stopped early" : "Folder contents could not be listed"
                : "Could not be inspected";
            RecordIssue(new ScanIssue(path, kind, err, $"{what}: {Win32Message(err)}"));
        }

        private void RecordIssue(ScanIssue issue)
        {
            _issues++;
            _sink.AddIssue(issue);
        }

        private static string Win32Message(int err) => err switch
        {
            ERROR_ACCESS_DENIED => "access denied (needs administrator rights or belongs to another user/the system)",
            _ => new System.ComponentModel.Win32Exception(err).Message,
        };

        private List<DirectoryTotals> ComputeTotals()
        {
            // Every folder gets its id after its parent, so walking ids from high to low folds each subtree
            // into its parent after all of its own descendants have been folded in.
            var ids = new long[_dirs.Count];
            _dirs.Keys.CopyTo(ids, 0);
            Array.Sort(ids);
            var result = new List<DirectoryTotals>(ids.Length);
            for (int i = ids.Length - 1; i >= 0; i--)
            {
                long id = ids[i];
                DirAgg a = _dirs[id];
                if (a.ParentId is long p && _dirs.TryGetValue(p, out DirAgg? parent))
                {
                    parent.Size += a.Size;
                    parent.SizeOnDisk += a.SizeOnDisk;
                    parent.Files += a.Files;
                    parent.Dirs += a.Dirs;
                }
                result.Add(new DirectoryTotals(id, a.Size, a.SizeOnDisk, a.Files, a.Dirs, a.Flags & EntryFlags.EnumerationFailed));
            }
            return result;
        }

        private void Report(ScanPhase phase, bool force = false)
        {
            if (_progress is null) return;
            TimeSpan now = _clock.Elapsed;
            if (!force && now - _lastReport < ProgressInterval) return;
            _lastReport = now;

            double? fraction = null;
            if (_volumeUsedBytes is > 0 && phase == ScanPhase.Enumerating)
                fraction = Math.Min(0.99, (double)_bytesOnDisk / _volumeUsedBytes.Value);
            else if (phase is ScanPhase.Done) fraction = 1;

            _progress.Report(new ScanProgress(phase, _files, _dirCount, _bytes, _bytesOnDisk, _issues, _currentPath, now, fraction));
        }

        private static long GetClusterSize(string root)
        {
            string? volume = Path.GetPathRoot(root);
            if (volume is null) return 4096;
            if (!volume.EndsWith('\\')) volume += "\\";
            return GetDiskFreeSpace(volume, out uint spc, out uint bps, out _, out _) && spc > 0 && bps > 0
                ? (long)spc * bps
                : 4096;
        }
    }
}
