using Microsoft.Data.Sqlite;
using PuterScanny.Core.Abstractions;
using PuterScanny.Core.Models;

namespace PuterScanny.Storage;

/// <summary>Writes scan output with prepared statements inside large transactions (millions of rows per scan).</summary>
internal sealed class SqliteScanSink : IScanSink
{
    private const int BatchSize = 20_000;

    private readonly ScanDatabase _db;
    private readonly SqliteConnection _conn;
    private SqliteTransaction? _tx;
    private int _pending;

    private SqliteCommand? _insertEntry;
    private SqliteCommand? _insertStream;
    private SqliteCommand? _insertIssue;

    public SqliteScanSink(ScanDatabase db)
    {
        _db = db;
        _conn = db.Connection;
    }

    public void Begin(ScanSummary initial)
    {
        // WAL lets the analysis stage read entries on one connection while writing results on another.
        // synchronous=NORMAL in WAL mode keeps the file intact even if Windows crashes or the power fails
        // (at worst the last few seconds of work are redone), which is what makes interrupted scans resumable.
        _db.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -65536;");

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO session (id, root_path, mode, is_elevated, started_utc, status, cluster_size, machine_name, app_version, method)
                VALUES (1, $root, $mode, $elev, $start, $status, $cluster, $machine, $ver, $method)
                """;
            cmd.Parameters.AddWithValue("$root", initial.RootPath);
            cmd.Parameters.AddWithValue("$mode", (int)initial.Mode);
            cmd.Parameters.AddWithValue("$elev", initial.IsElevated ? 1 : 0);
            cmd.Parameters.AddWithValue("$start", ScanDatabase.ToTicks(initial.StartedUtc));
            cmd.Parameters.AddWithValue("$status", (int)initial.Status);
            cmd.Parameters.AddWithValue("$cluster", initial.ClusterSize);
            cmd.Parameters.AddWithValue("$machine", (object?)initial.MachineName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", (object?)initial.AppVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$method", (object?)initial.Method ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        BeginBatch();
    }

    public void AddEntry(FileSystemEntry e)
    {
        var p = _insertEntry!.Parameters;
        p["$id"].Value = e.Id;
        p["$parent"].Value = e.ParentId is long pid ? pid : DBNull.Value;
        p["$name"].Value = e.Name;
        p["$path"].Value = e.Path;
        p["$is_dir"].Value = e.IsDirectory ? 1 : 0;
        p["$depth"].Value = e.Depth;
        p["$size"].Value = e.Size;
        p["$sod"].Value = e.SizeOnDisk;
        p["$attr"].Value = (long)e.Attributes;
        p["$created"].Value = ScanDatabase.ToTicks(e.CreatedUtc);
        p["$modified"].Value = ScanDatabase.ToTicks(e.ModifiedUtc);
        p["$accessed"].Value = ScanDatabase.ToTicks(e.AccessedUtc);
        p["$tag"].Value = (long)e.ReparseTag;
        p["$flags"].Value = (long)e.Flags;
        p["$sbytes"].Value = e.StreamBytes;
        p["$target"].Value = (object?)e.LinkTarget ?? DBNull.Value;
        p["$tsize"].Value = e.TotalSize;
        p["$tsod"].Value = e.TotalSizeOnDisk;
        p["$files"].Value = e.FileCount;
        p["$dirs"].Value = e.DirectoryCount;
        _insertEntry.ExecuteNonQuery();
        Tick();
    }

    public void AddStream(AlternateStream s)
    {
        var p = _insertStream!.Parameters;
        p["$id"].Value = s.EntryId;
        p["$name"].Value = s.Name;
        p["$size"].Value = s.Size;
        _insertStream.ExecuteNonQuery();
        Tick();
    }

    public void AddIssue(ScanIssue issue)
    {
        var p = _insertIssue!.Parameters;
        p["$path"].Value = issue.Path;
        p["$kind"].Value = (int)issue.Kind;
        p["$err"].Value = issue.Win32Error;
        p["$msg"].Value = issue.Message;
        _insertIssue.ExecuteNonQuery();
        Tick();
    }

    public void SetDirectoryTotals(IEnumerable<DirectoryTotals> totals)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = """
            UPDATE entry SET total_size = $ts, total_size_on_disk = $tsod, file_count = $fc, dir_count = $dc, flags = flags | $xf
            WHERE id = $id
            """;
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var tsod = cmd.Parameters.Add("$tsod", SqliteType.Integer);
        var fc = cmd.Parameters.Add("$fc", SqliteType.Integer);
        var dc = cmd.Parameters.Add("$dc", SqliteType.Integer);
        var xf = cmd.Parameters.Add("$xf", SqliteType.Integer);
        cmd.Prepare();

        foreach (var t in totals)
        {
            id.Value = t.Id;
            ts.Value = t.TotalSize;
            tsod.Value = t.TotalSizeOnDisk;
            fc.Value = t.FileCount;
            dc.Value = t.DirectoryCount;
            xf.Value = (long)t.ExtraFlags;
            cmd.ExecuteNonQuery();
            if (++_pending >= BatchSize)
            {
                CommitBatch();
                BeginBatch();
                cmd.Transaction = _tx;
            }
        }
    }

    public void Complete(ScanSummary final)
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = _tx;
            cmd.CommandText = """
                UPDATE session SET completed_utc = $done, status = $status, file_count = $fc, dir_count = $dc,
                    total_size = $ts, total_size_on_disk = $tsod, issue_count = $ic, stream_count = $sc, stream_bytes = $sb,
                    error_message = $err
                WHERE id = 1
                """;
            cmd.Parameters.AddWithValue("$done", final.CompletedUtc is { } d ? ScanDatabase.ToTicks(d) : DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (int)final.Status);
            cmd.Parameters.AddWithValue("$fc", final.FileCount);
            cmd.Parameters.AddWithValue("$dc", final.DirectoryCount);
            cmd.Parameters.AddWithValue("$ts", final.TotalSize);
            cmd.Parameters.AddWithValue("$tsod", final.TotalSizeOnDisk);
            cmd.Parameters.AddWithValue("$ic", final.IssueCount);
            cmd.Parameters.AddWithValue("$sc", final.StreamCount);
            cmd.Parameters.AddWithValue("$sb", final.StreamBytes);
            cmd.Parameters.AddWithValue("$err", (object?)final.ErrorMessage ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        CommitBatch();
        _insertEntry?.Dispose();
        _insertStream?.Dispose();
        _insertIssue?.Dispose();
        _db.Execute(Schema.CreateIndexes);
    }

    private void Tick()
    {
        if (++_pending < BatchSize) return;
        CommitBatch();
        BeginBatch();
    }

    private void BeginBatch()
    {
        _tx = _conn.BeginTransaction();
        _pending = 0;

        _insertEntry ??= Prepare("""
            INSERT INTO entry (id, parent_id, name, path, is_dir, depth, size, size_on_disk, attributes, created_utc, modified_utc,
                accessed_utc, reparse_tag, flags, stream_bytes, link_target, total_size, total_size_on_disk, file_count, dir_count)
            VALUES ($id, $parent, $name, $path, $is_dir, $depth, $size, $sod, $attr, $created, $modified,
                $accessed, $tag, $flags, $sbytes, $target, $tsize, $tsod, $files, $dirs)
            """,
            "$id", "$parent", "$name", "$path", "$is_dir", "$depth", "$size", "$sod", "$attr", "$created", "$modified",
            "$accessed", "$tag", "$flags", "$sbytes", "$target", "$tsize", "$tsod", "$files", "$dirs");
        _insertStream ??= Prepare("INSERT INTO stream (entry_id, name, size) VALUES ($id, $name, $size)", "$id", "$name", "$size");
        _insertIssue ??= Prepare("INSERT INTO issue (path, kind, win32_error, message) VALUES ($path, $kind, $err, $msg)", "$path", "$kind", "$err", "$msg");

        _insertEntry.Transaction = _tx;
        _insertStream.Transaction = _tx;
        _insertIssue.Transaction = _tx;
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach (string name in parameters) cmd.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        cmd.Prepare();
        return cmd;
    }

    private void CommitBatch()
    {
        _tx?.Commit();
        _tx?.Dispose();
        _tx = null;
        _pending = 0;
    }
}
