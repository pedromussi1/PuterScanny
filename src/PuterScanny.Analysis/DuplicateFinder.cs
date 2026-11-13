using System.Diagnostics;
using System.Security.Cryptography;
using PuterScanny.Core.Models;
using PuterScanny.Signals.Content;
using PuterScanny.Storage;

namespace PuterScanny.Analysis;

/// <summary>
/// Finds files with identical contents: same size → same fingerprint of the first and last 64 KB → same full
/// SHA-256. Only files PuterScanny could ever suggest removing are compared (nothing Windows or a program needs).
/// </summary>
public sealed class DuplicateFinder
{
    public const long MinSize = 64 * 1024;          // smaller duplicates are not worth anyone's time
    private const int EdgeBytes = 64 * 1024;

    public sealed record Candidate(long Id, string Path, long Size, DateTime Created, DateTime? LastUsed, string? Rule, string? Category);

    private static readonly HashSet<string> MediaCategories = new(StringComparer.Ordinal)
    {
        "image", "video", "audio", "document", "ebook", "email", "design", "archive", "disk-image", "installer",
    };

    public sealed record Group(long Size, IReadOnlyList<Candidate> Files);

    private readonly bool _gentle;
    private readonly RateLimiter _limiter;

    public DuplicateFinder(bool gentle)
    {
        _gentle = gentle;
        _limiter = new RateLimiter(gentle ? 200 : 0);
    }

    public List<Group> Find(ScanDatabase db, IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        var groups = new List<Group>();
        bool background = _gentle && BackgroundThread.Enter();
        try
        {
            var bySize = LoadCandidates(db, ct);
            long totalSets = bySize.Count, doneSets = 0, filesRead = 0;
            var clock = Stopwatch.StartNew();
            TimeSpan last = TimeSpan.Zero;

            foreach (var (size, files) in bySize)
            {
                ct.ThrowIfCancellationRequested();
                doneSets++;

                // 1. Cheap fingerprint: size + first and last 64 KB.
                var byEdges = new Dictionary<string, List<Candidate>>();
                foreach (var f in files)
                {
                    _limiter.Wait(ct);
                    filesRead++;
                    string? key = Hash(f.Path, edgesOnly: size > 2 * EdgeBytes, ct);
                    if (key is null) continue;
                    if (!byEdges.TryGetValue(key, out var list)) byEdges[key] = list = [];
                    list.Add(f);
                }

                // 2. Full SHA-256 for files whose edges match (small files were hashed completely already).
                foreach (var sameEdges in byEdges.Values.Where(l => l.Count > 1))
                {
                    if (size <= 2 * EdgeBytes)
                    {
                        groups.Add(new Group(size, sameEdges));
                        continue;
                    }
                    var byFull = new Dictionary<string, List<Candidate>>();
                    foreach (var f in sameEdges)
                    {
                        _limiter.Wait(ct);
                        string? full = Hash(f.Path, edgesOnly: false, ct);
                        if (full is null) continue;
                        if (!byFull.TryGetValue(full, out var list)) byFull[full] = list = [];
                        list.Add(f);
                    }
                    groups.AddRange(byFull.Values.Where(l => l.Count > 1).Select(l => new Group(size, l)));
                }

                if (clock.Elapsed - last > TimeSpan.FromMilliseconds(250))
                {
                    last = clock.Elapsed;
                    progress?.Report(new PipelineProgress(ScanPhase.FindingDuplicates, "Looking for duplicate files…",
                        $"{doneSets:N0} of {totalSets:N0} same-size sets compared  ·  {filesRead:N0} files read  ·  {groups.Count:N0} duplicate sets found",
                        (double)doneSets / Math.Max(1, totalSets), files[0].Path));
                }
            }
        }
        finally
        {
            if (background) BackgroundThread.Leave();
        }
        return groups;
    }

    /// <summary>
    /// Identical copies that all live inside one program, game or project folder (for example the same sound bank for
    /// several languages) are how that software is built: removing one would break it. Those are only reported when
    /// they are your media or documents.
    /// </summary>
    public static bool IsMeaningful(IReadOnlyList<Candidate> files, string usersFolder)
    {
        if (files.Any(f => f.Category is { } c && MediaCategories.Contains(c))) return true;
        string common = CommonFolder(files.Select(f => f.Path));
        string anchor = Anchor(files[0].Path, usersFolder);
        return common.Length <= anchor.Length; // copies spread across your folders, not nested inside one package
    }

    private static string CommonFolder(IEnumerable<string> paths)
    {
        string[]? common = null;
        foreach (string p in paths)
        {
            string[] parts = (System.IO.Path.GetDirectoryName(p) ?? p).Split('\\');
            if (common is null) { common = parts; continue; }
            int n = 0;
            while (n < common.Length && n < parts.Length && string.Equals(common[n], parts[n], StringComparison.OrdinalIgnoreCase)) n++;
            common = common[..n];
        }
        return common is null ? "" : string.Join('\\', common);
    }

    /// <summary>The top of "your" area for a path: a folder directly in your profile (Downloads, Documents…) or the drive root.</summary>
    private static string Anchor(string path, string usersFolder)
    {
        string users = usersFolder.TrimEnd('\\') + "\\";
        if (path.StartsWith(users, StringComparison.OrdinalIgnoreCase))
        {
            string[] rest = path[users.Length..].Split('\\');
            if (rest.Length >= 3) return users + rest[0] + "\\" + rest[1];
        }
        return System.IO.Path.GetPathRoot(path)?.TrimEnd('\\') ?? "";
    }

    /// <summary>The copy to keep: the one in your own folders, the one you used most recently, then the oldest (the original).</summary>
    public static Candidate ChooseKeeper(IReadOnlyList<Candidate> files) =>
        files.OrderByDescending(f => KeeperScore(f.Rule))
             .ThenByDescending(f => f.LastUsed ?? DateTime.MinValue)
             .ThenBy(f => f.Created == default ? DateTime.MaxValue : f.Created)
             .ThenBy(f => f.Path.Length)
             .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
             .First();

    private static int KeeperScore(string? rule) => rule switch
    {
        "user.documents" or "user.pictures" or "user.videos" or "user.music" or "user.desktop" => 3,
        "user.onedrive" or "user.dropbox" or "user.googledrive" or "user.icloud" => 3,
        null or "user.profile" => 2,
        "user.downloads" => 1,
        "user.temp" or "win.temp" or "win.recyclebin" => -1,
        _ => 0,
    };

    private static SortedDictionary<long, List<Candidate>> LoadCandidates(ScanDatabase db, CancellationToken ct)
    {
        var result = new SortedDictionary<long, List<Candidate>>(Comparer<long>.Create((a, b) => b.CompareTo(a))); // biggest first
        using var read = db.OpenReadConnection();
        using var cmd = read.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.id, e.path, e.size, e.created_utc, a.last_used, a.location_rule, a.category
            FROM entry e JOIN analysis a ON a.entry_id = e.id
            WHERE e.is_dir = 0 AND e.size >= {MinSize}
              AND a.protection < {(int)Protection.UninstallOnly}
              AND (e.flags & {(int)(EntryFlags.CloudPlaceholder | EntryFlags.ReparsePointNotFollowed | EntryFlags.HardLinkSecondary)}) = 0
              AND e.size IN (
                  SELECT e2.size FROM entry e2 JOIN analysis a2 ON a2.entry_id = e2.id
                  WHERE e2.is_dir = 0 AND e2.size >= {MinSize} AND a2.protection < {(int)Protection.UninstallOnly}
                  GROUP BY e2.size HAVING COUNT(*) > 1)
            """;
        using var r = cmd.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            if ((++n & 4095) == 0) ct.ThrowIfCancellationRequested();
            var c = new Candidate(r.GetInt64(0), r.GetString(1), r.GetInt64(2),
                r.GetInt64(3) > 0 ? new DateTime(r.GetInt64(3), DateTimeKind.Utc) : default,
                r.IsDBNull(4) ? null : new DateTime(r.GetInt64(4), DateTimeKind.Utc),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6));
            if (!result.TryGetValue(c.Size, out var list)) result[c.Size] = list = [];
            list.Add(c);
        }
        foreach (long size in result.Where(kv => kv.Value.Count < 2).Select(kv => kv.Key).ToList()) result.Remove(size);
        return result;
    }

    private static string? Hash(string path, bool edgesOnly, CancellationToken ct)
    {
        var (handle, _) = SafeFileReader.Open(path);
        if (handle is null) return null;
        using (handle)
        {
            try
            {
                long size = RandomAccess.GetLength(handle);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1024 * 1024];
                if (edgesOnly)
                {
                    int head = SafeFileReader.ReadAt(handle, 0, buffer.AsSpan(0, EdgeBytes));
                    sha.AppendData(buffer, 0, head);
                    int tail = SafeFileReader.ReadAt(handle, size - EdgeBytes, buffer.AsSpan(0, EdgeBytes));
                    sha.AppendData(buffer, 0, tail);
                }
                else
                {
                    long pos = 0;
                    while (pos < size)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = SafeFileReader.ReadAt(handle, pos, buffer);
                        if (n <= 0) break;
                        sha.AppendData(buffer, 0, n);
                        pos += n;
                    }
                }
                return size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(sha.GetHashAndReset());
            }
            catch (IOException)
            {
                return null; // changed or locked while reading: not reported as a duplicate
            }
        }
    }
}
