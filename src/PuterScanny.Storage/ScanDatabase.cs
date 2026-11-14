using System.Globalization;
using Microsoft.Data.Sqlite;
using PuterScanny.Core.Abstractions;
using PuterScanny.Core.Models;

namespace PuterScanny.Storage;

/// <summary>
/// One SQLite file per scan. Holds every entry, hidden stream and issue so results can be browsed,
/// compared and exported later without rescanning.
/// </summary>
public sealed partial class ScanDatabase : IDisposable
{
    public const int SchemaVersion = 7;

    private readonly SqliteConnection _connection;

    private ScanDatabase(string filePath, SqliteConnection connection)
    {
        FilePath = filePath;
        _connection = connection;
    }

    public string FilePath { get; }

    /// <summary>%LOCALAPPDATA%\PuterScanny\Scans</summary>
    public static string DefaultScansFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuterScanny", "Scans");

    public static string NewScanFilePath(string? folder = null)
    {
        folder ??= DefaultScansFolder;
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(folder, $"scan-{stamp}.db");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"scan-{stamp}-{i}.db");
        return path;
    }

    public static ScanDatabase Create(string filePath)
    {
        if (File.Exists(filePath)) throw new IOException($"Scan database already exists: {filePath}");
        var db = new ScanDatabase(filePath, OpenConnection(filePath, SqliteOpenMode.ReadWriteCreate));
        db.Execute(Schema.Create);
        db.Execute($"INSERT INTO meta(key, value) VALUES ('schema_version', '{SchemaVersion}');");
        return db;
    }

    public static ScanDatabase Open(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Scan database not found.", filePath);
        return new ScanDatabase(filePath, OpenConnection(filePath, SqliteOpenMode.ReadWrite));
    }

    private static SqliteConnection OpenConnection(string filePath, SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = filePath, Mode = mode, Pooling = false }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        return connection;
    }

    /// <summary>Creates a sink that writes a scan into this database in large batched transactions.</summary>
    public IScanSink CreateSink() => new SqliteScanSink(this);

    internal SqliteConnection Connection => _connection;

    internal void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- queries

    public ScanSummary? GetSummary()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT root_path, mode, is_elevated, started_utc, completed_utc, status, file_count, dir_count, total_size, total_size_on_disk, issue_count, stream_count, stream_bytes, cluster_size, machine_name, app_version, error_message, method FROM session WHERE id = 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ScanSummary
        {
            RootPath = r.GetString(0),
            Mode = (ScanMode)r.GetInt32(1),
            IsElevated = r.GetInt32(2) != 0,
            StartedUtc = new DateTime(r.GetInt64(3), DateTimeKind.Utc),
            CompletedUtc = r.IsDBNull(4) ? null : new DateTime(r.GetInt64(4), DateTimeKind.Utc),
            Status = (ScanStatus)r.GetInt32(5),
            FileCount = r.GetInt64(6),
            DirectoryCount = r.GetInt64(7),
            TotalSize = r.GetInt64(8),
            TotalSizeOnDisk = r.GetInt64(9),
            IssueCount = r.GetInt64(10),
            StreamCount = r.GetInt64(11),
            StreamBytes = r.GetInt64(12),
            ClusterSize = r.GetInt64(13),
            MachineName = r.IsDBNull(14) ? null : r.GetString(14),
            AppVersion = r.IsDBNull(15) ? null : r.GetString(15),
            ErrorMessage = r.IsDBNull(16) ? null : r.GetString(16),
            Method = r.IsDBNull(17) ? null : r.GetString(17),
        };
    }

    private const string EntrySelect = """
        SELECT e.id, e.parent_id, e.name, e.path, e.is_dir, e.depth, e.size, e.size_on_disk, e.attributes, e.created_utc,
               e.modified_utc, e.accessed_utc, e.reparse_tag, e.flags, e.stream_bytes, e.link_target, e.total_size,
               e.total_size_on_disk, e.file_count, e.dir_count,
               a.owner_app_id, ap.name, a.owner_exact, a.location_rule, a.category, a.type_label, a.format_id, a.mismatch,
               a.read_status, a.entry_id, a.protection, a.contained_protection,
               a.last_used, a.use_count, a.active_seconds, a.contained_last_used,
               a.contained_last_modified, a.verdict, a.confidence, a.action, a.headline, a.fact_flags,
               a.reclaim_bytes, a.review_bytes, a.tool_bytes, a.top_reasons
        FROM entry e
        LEFT JOIN analysis a ON a.entry_id = e.id
        LEFT JOIN app ap ON ap.id = a.owner_app_id
        """;

    public FileSystemEntry? GetEntry(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{EntrySelect} WHERE e.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntry(r) : null;
    }

    public FileSystemEntry? GetRoot() => GetEntry(1);

    /// <summary>Every entry with its analysis, parents before children, read through a separate connection.</summary>
    public static IEnumerable<FileSystemEntry> ReadAllEntries(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"{EntrySelect} ORDER BY e.id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return ReadEntry(r);
    }

    /// <summary>Children of a folder, biggest first.</summary>
    public IReadOnlyList<FileSystemEntry> GetChildren(long parentId, int limit = int.MaxValue, int offset = 0)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{EntrySelect} WHERE e.parent_id = $p ORDER BY e.total_size DESC, e.name LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$p", parentId);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return ReadEntries(cmd);
    }

    public (long Count, long TotalSize) GetChildrenStats(long parentId, int skipLargest)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(total_size), 0) FROM (SELECT total_size FROM entry WHERE parent_id = $p ORDER BY total_size DESC, name LIMIT -1 OFFSET $skip)";
        cmd.Parameters.AddWithValue("$p", parentId);
        cmd.Parameters.AddWithValue("$skip", skipLargest);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    public bool HasChildren(long parentId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM entry WHERE parent_id = $p)";
        cmd.Parameters.AddWithValue("$p", parentId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public IReadOnlyList<FileSystemEntry> GetLargestFiles(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{EntrySelect} WHERE e.is_dir = 0 ORDER BY e.size DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(cmd);
    }

    /// <summary>
    /// The biggest items nothing identifies (no owning program, no known location, unknown file type), highest
    /// level only: when a folder is itself unidentified, its contents aren't listed separately.
    /// </summary>
    public IReadOnlyList<FileSystemEntry> GetUnidentifiedItems(int limit, long minBytes)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            {EntrySelect}
            JOIN entry pe ON pe.id = e.parent_id
            LEFT JOIN analysis pa ON pa.entry_id = e.parent_id
            WHERE e.depth > 0 AND a.entry_id IS NOT NULL
              AND a.owner_app_id IS NULL AND a.location_rule IS NULL
              AND (e.is_dir = 1 OR IFNULL(a.category, 'other') = 'other')
              AND e.total_size >= $min
              AND e.id NOT IN (SELECT entry_id FROM removed)
              AND NOT (pe.depth > 0 AND pa.entry_id IS NOT NULL AND pa.owner_app_id IS NULL AND pa.location_rule IS NULL)
            ORDER BY e.total_size DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$min", minBytes);
        return ReadEntries(cmd);
    }

    public IReadOnlyList<AlternateStream> GetStreams(long entryId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT entry_id, name, size FROM stream WHERE entry_id = $id ORDER BY name";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        var list = new List<AlternateStream>();
        while (r.Read()) list.Add(new AlternateStream(r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        return list;
    }

    public IReadOnlyList<ScanIssue> GetIssues(int limit = int.MaxValue)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, kind, win32_error, message FROM issue ORDER BY kind, path LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ScanIssue>();
        while (r.Read()) list.Add(new ScanIssue(r.GetString(0), (ScanIssueKind)r.GetInt32(1), r.GetInt32(2), r.GetString(3)));
        return list;
    }

    private static List<FileSystemEntry> ReadEntries(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<FileSystemEntry>();
        while (r.Read()) list.Add(ReadEntry(r));
        return list;
    }

    private static FileSystemEntry ReadEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ParentId = r.IsDBNull(1) ? null : r.GetInt64(1),
        Name = r.GetString(2),
        Path = r.GetString(3),
        IsDirectory = r.GetInt32(4) != 0,
        Depth = r.GetInt32(5),
        Size = r.GetInt64(6),
        SizeOnDisk = r.GetInt64(7),
        Attributes = (FileAttributes)r.GetInt64(8),
        CreatedUtc = FromTicks(r.GetInt64(9)),
        ModifiedUtc = FromTicks(r.GetInt64(10)),
        AccessedUtc = FromTicks(r.GetInt64(11)),
        ReparseTag = (uint)r.GetInt64(12),
        Flags = (EntryFlags)r.GetInt64(13),
        StreamBytes = r.GetInt64(14),
        LinkTarget = r.IsDBNull(15) ? null : r.GetString(15),
        TotalSize = r.GetInt64(16),
        TotalSizeOnDisk = r.GetInt64(17),
        FileCount = r.GetInt64(18),
        DirectoryCount = r.GetInt64(19),
        Analysis = r.IsDBNull(29) ? null : new AnalysisInfo
        {
            OwnerAppId = r.IsDBNull(20) ? null : r.GetInt64(20),
            OwnerAppName = r.IsDBNull(21) ? null : r.GetString(21),
            OwnerIsExact = r.GetInt32(22) != 0,
            LocationRuleId = r.IsDBNull(23) ? null : r.GetString(23),
            Category = r.IsDBNull(24) ? null : r.GetString(24),
            TypeLabel = r.IsDBNull(25) ? null : r.GetString(25),
            FormatId = r.IsDBNull(26) ? null : r.GetString(26),
            ExtensionMismatch = r.GetInt32(27) != 0,
            ReadStatus = (ReadStatus)r.GetInt32(28),
            Protection = (Protection)r.GetInt32(30),
            ContainedProtection = (Protection)r.GetInt32(31),
            LastUsedUtc = r.IsDBNull(32) ? null : new DateTime(r.GetInt64(32), DateTimeKind.Utc),
            UseCount = r.GetInt32(33),
            ActiveTime = TimeSpan.FromSeconds(r.GetInt64(34)),
            ContainedLastUsedUtc = r.IsDBNull(35) ? null : new DateTime(r.GetInt64(35), DateTimeKind.Utc),
            ContainedLastModifiedUtc = r.IsDBNull(36) ? null : new DateTime(r.GetInt64(36), DateTimeKind.Utc),
            Verdict = r.IsDBNull(37) ? null : (Verdict)r.GetInt32(37),
            Confidence = r.GetInt32(38),
            Action = (CleanupAction)r.GetInt32(39),
            Headline = r.IsDBNull(40) ? null : r.GetString(40),
            Facts = (FactFlags)r.GetInt32(41),
            ReclaimBytes = r.GetInt64(42),
            ReviewBytes = r.GetInt64(43),
            ToolBytes = r.GetInt64(44),
            TopReasons = r.IsDBNull(45) ? null : r.GetString(45),
        },
    };

    // ---------------------------------------------------------------- analysis results

    public AnalysisWriter CreateAnalysisWriter() => new(this);

    /// <summary>A second, read-only connection for streaming entries while results are written on the main one.</summary>
    public SqliteConnection OpenReadConnection()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = FilePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    /// <summary>Folds the write-ahead log back in and leaves a single, durable database file.</summary>
    public void Finish()
    {
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        Execute("PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL;");
    }

    public string? GetMeta(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string? value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool HasAnalysis => GetMeta("analysis.status") is not null;

    /// <summary>Files whose content has already been looked at (any outcome, including "locked").</summary>
    public long CountIdentifiedFiles()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM analysis a JOIN entry e ON e.id = a.entry_id WHERE e.is_dir = 0 AND a.read_status <> 0";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private const string AppColumns =
        "id, name, publisher, version, install_location, install_date, estimated_size, source, uninstall_command, is_system_component, is_framework, source_key, extra_folders, owned_file_count, measured_bytes, measured_files, last_used, use_count, active_seconds, uninstall_confidence, uninstall_reason";

    public IReadOnlyList<InstalledApp> GetApps()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {AppColumns} FROM app ORDER BY measured_bytes DESC, name";
        using var r = cmd.ExecuteReader();
        var list = new List<InstalledApp>();
        while (r.Read()) list.Add(ReadApp(r));
        return list;
    }

    public InstalledApp? GetApp(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {AppColumns} FROM app WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadApp(r) : null;
    }

    private static InstalledApp ReadApp(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Publisher = r.IsDBNull(2) ? null : r.GetString(2),
        Version = r.IsDBNull(3) ? null : r.GetString(3),
        InstallLocation = r.IsDBNull(4) ? null : r.GetString(4),
        InstallDate = r.IsDBNull(5) ? null : new DateTime(r.GetInt64(5), DateTimeKind.Utc),
        EstimatedSizeBytes = r.IsDBNull(6) ? null : r.GetInt64(6),
        Source = (AppSource)r.GetInt32(7),
        UninstallCommand = r.IsDBNull(8) ? null : r.GetString(8),
        IsSystemComponent = r.GetInt32(9) != 0,
        IsFramework = r.GetInt32(10) != 0,
        SourceKey = r.IsDBNull(11) ? null : r.GetString(11),
        ExtraFolders = r.IsDBNull(12) ? [] : r.GetString(12).Split('|', StringSplitOptions.RemoveEmptyEntries),
        OwnedFileCount = r.GetInt64(13),
        MeasuredBytes = r.GetInt64(14),
        MeasuredFiles = r.GetInt64(15),
        LastUsedUtc = r.IsDBNull(16) ? null : new DateTime(r.GetInt64(16), DateTimeKind.Utc),
        UseCount = r.GetInt32(17),
        ActiveTime = TimeSpan.FromSeconds(r.GetInt64(18)),
        UninstallConfidence = r.GetInt32(19),
        UninstallReason = r.IsDBNull(20) ? null : r.GetString(20),
    };

    public PeInfo? GetPeInfo(long entryId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT machine, subsystem, is_dll, is_driver, is_dotnet, link_time, company, product, description, file_version,
                   product_version, original_filename, internal_name, copyright, has_embedded_sig, sig_state, sig_kind, signed_by, sig_error
            FROM pe_info WHERE entry_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new PeInfo
        {
            Machine = S(0) ?? "",
            Subsystem = S(1) ?? "",
            IsDll = r.GetInt32(2) != 0,
            IsDriver = r.GetInt32(3) != 0,
            IsDotNet = r.GetInt32(4) != 0,
            LinkTimeUtc = r.IsDBNull(5) ? null : new DateTime(r.GetInt64(5), DateTimeKind.Utc),
            CompanyName = S(6),
            ProductName = S(7),
            FileDescription = S(8),
            FileVersion = S(9),
            ProductVersion = S(10),
            OriginalFilename = S(11),
            InternalName = S(12),
            LegalCopyright = S(13),
            HasEmbeddedSignature = r.GetInt32(14) != 0,
            SignatureState = (SignatureState)r.GetInt32(15),
            SignatureKind = (SignatureKind)r.GetInt32(16),
            SignedBy = S(17),
            SignatureError = r.GetInt32(18),
        };
    }

    public IReadOnlyList<CategoryStat> GetCategoryStats()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(a.category, 'other'), COUNT(*), COALESCE(SUM(e.size), 0)
            FROM entry e JOIN analysis a ON a.entry_id = e.id
            WHERE e.is_dir = 0
            GROUP BY 1 ORDER BY 3 DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<CategoryStat>();
        while (r.Read()) list.Add(new CategoryStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public IReadOnlyList<ExtensionStat> GetExtensionStats(int limit = int.MaxValue)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT ext, files, bytes, label, category, known, windows_type_name, opens_with FROM extension_info ORDER BY bytes DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ExtensionStat>();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read()) list.Add(new ExtensionStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2), S(3), S(4), r.GetInt32(5) != 0, S(6), S(7)));
        return list;
    }

    public ExtensionStat? GetExtensionStat(string ext)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT ext, files, bytes, label, category, known, windows_type_name, opens_with FROM extension_info WHERE ext = $e";
        cmd.Parameters.AddWithValue("$e", ext);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new ExtensionStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2), S(3), S(4), r.GetInt32(5) != 0, S(6), S(7));
    }

    public IReadOnlyList<SystemReference> GetReferences(long entryId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT kind, level, detail FROM reference WHERE entry_id = $id ORDER BY level DESC, kind";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        var list = new List<SystemReference>();
        while (r.Read()) list.Add(new SystemReference((ReferenceKind)r.GetInt32(0), (Protection)r.GetInt32(1), r.GetString(2)));
        return list;
    }

    /// <summary>Every recorded dependency, strongest first, with the item's path.</summary>
    public IReadOnlyList<(string Path, SystemReference Reference)> GetAllReferences(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.path, r.kind, r.level, r.detail FROM reference r JOIN entry e ON e.id = r.entry_id
            ORDER BY r.level DESC, r.kind, e.path LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, SystemReference)>();
        while (rd.Read()) list.Add((rd.GetString(0), new SystemReference((ReferenceKind)rd.GetInt32(1), (Protection)rd.GetInt32(2), rd.GetString(3))));
        return list;
    }

    /// <summary>Files per protection level, with their total size.</summary>
    public IReadOnlyList<(Protection Level, long Files, long Bytes)> GetProtectionStats()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.protection, COUNT(*), COALESCE(SUM(e.size), 0)
            FROM analysis a JOIN entry e ON e.id = a.entry_id WHERE e.is_dir = 0
            GROUP BY 1 ORDER BY 1 DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<(Protection, long, long)>();
        while (r.Read()) list.Add(((Protection)r.GetInt32(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public IReadOnlyList<UsageEvent> GetUsage(long entryId) => ReadUsage("SELECT source, last_used, count, active_seconds, detail FROM usage WHERE entry_id = $id ORDER BY last_used DESC", entryId);

    public IReadOnlyList<UsageEvent> GetAppUsage(long appId) => ReadUsage("SELECT source, last_used, count, active_seconds, detail FROM app_usage WHERE app_id = $id ORDER BY last_used DESC", appId);

    private IReadOnlyList<UsageEvent> ReadUsage(string sql, long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var list = new List<UsageEvent>();
        while (r.Read())
        {
            list.Add(new UsageEvent((UsageSource)r.GetInt32(0),
                r.IsDBNull(1) ? null : new DateTime(r.GetInt64(1), DateTimeKind.Utc),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : TimeSpan.FromSeconds(r.GetInt64(3)),
                r.GetString(4)));
        }
        return list;
    }

    public IReadOnlyList<UsageSourceStatus> GetUsageSources()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT source, available, items, note FROM usage_source ORDER BY source";
        using var r = cmd.ExecuteReader();
        var list = new List<UsageSourceStatus>();
        while (r.Read()) list.Add(new UsageSourceStatus((UsageSource)r.GetInt32(0), r.GetInt32(1) != 0, r.GetInt32(2), r.GetString(3)));
        return list;
    }

    /// <summary>Files in the scan with a usage record, most recently used first.</summary>
    public IReadOnlyList<FileSystemEntry> GetRecentlyUsedFiles(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{EntrySelect} WHERE e.is_dir = 0 AND a.last_used IS NOT NULL ORDER BY a.last_used DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(cmd);
    }

    /// <summary>Files per verdict with their total size (only files count, so nothing is counted twice).</summary>
    public IReadOnlyList<(Verdict Verdict, CleanupAction Action, long Files, long Bytes)> GetVerdictStats()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.verdict, a.action, COUNT(*), COALESCE(SUM(e.size), 0)
            FROM analysis a JOIN entry e ON e.id = a.entry_id
            WHERE e.is_dir = 0 AND a.verdict IS NOT NULL
            GROUP BY 1, 2 ORDER BY 1 DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<(Verdict, CleanupAction, long, long)>();
        while (r.Read()) list.Add(((Verdict)r.GetInt32(0), (CleanupAction)r.GetInt32(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    /// <summary>Files kept only because nothing pointed either way (part of the "Keep" total).</summary>
    public (long Files, long Bytes) GetNoEvidenceStats()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(e.size), 0) FROM analysis a JOIN entry e ON e.id = a.entry_id
            WHERE e.is_dir = 0 AND a.verdict = {(int)Verdict.Keep} AND (a.fact_flags & {(int)FactFlags.NoEvidence}) <> 0
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    /// <summary>
    /// The highest-level items with one of these verdicts (a deletable folder is listed once, not file by file), biggest first.
    /// </summary>
    public IReadOnlyList<FileSystemEntry> GetTopItems(IReadOnlyCollection<Verdict> verdicts, int limit, CleanupAction? action = null)
    {
        string list = string.Join(",", verdicts.Select(v => (int)v));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            {EntrySelect}
            LEFT JOIN analysis pa ON pa.entry_id = e.parent_id
            WHERE a.verdict IN ({list}) {(action is null ? "" : $"AND a.action = {(int)action}")}
              AND e.id NOT IN (SELECT entry_id FROM removed)
              AND (pa.verdict IS NULL OR pa.verdict NOT IN ({list}) {(action is null ? "" : $"OR pa.action <> {(int)action}")})
            ORDER BY e.total_size DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(cmd);
    }

    public (string KeeperPath, int Copies, bool IsKeeper, long GroupId)? GetDuplicateInfo(long entryId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT keeper_path, copies, is_keeper, group_id FROM duplicate WHERE entry_id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt32(1), r.GetInt32(2) != 0, r.GetInt64(3)) : null;
    }

    public IReadOnlyList<string> GetDuplicatePaths(long groupId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT e.path FROM duplicate d JOIN entry e ON e.id = d.entry_id WHERE d.group_id = $g ORDER BY d.is_keeper DESC, e.path";
        cmd.Parameters.AddWithValue("$g", groupId);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Duplicate sets, most wasted space first.</summary>
    public IReadOnlyList<DuplicateGroup> GetDuplicateGroups(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.group_id, MAX(e.size), COUNT(*), MAX(d.keeper_path), GROUP_CONCAT(CASE WHEN d.is_keeper = 0 THEN e.path END, '|')
            FROM duplicate d JOIN entry e ON e.id = d.entry_id
            GROUP BY d.group_id ORDER BY MAX(e.size) * (COUNT(*) - 1) DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<DuplicateGroup>();
        while (r.Read())
        {
            long size = r.GetInt64(1);
            int copies = r.GetInt32(2);
            list.Add(new DuplicateGroup(r.GetInt64(0), size, copies, size * (copies - 1), r.GetString(3),
                r.IsDBNull(4) ? [] : r.GetString(4).Split('|')));
        }
        return list;
    }

    /// <summary>Records items removed by a clean-up (so lists stop offering them).</summary>
    public void MarkRemoved(IEnumerable<long> entryIds, string batchId, int method)
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO removed (entry_id, batch_id, method, removed_utc) VALUES ($id, $b, $m, $t)";
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$m", method);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
        foreach (long e in entryIds)
        {
            id.Value = e;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UnmarkRemoved(IEnumerable<long> entryIds)
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM removed WHERE entry_id = $id";
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (long e in entryIds)
        {
            id.Value = e;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public HashSet<long> GetRemovedIds()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT entry_id FROM removed";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    public long GetRemovedBytes()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(e.total_size), 0) FROM removed r JOIN entry e ON e.id = r.entry_id";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<string> GetInventoryProblems()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT message FROM inventory_problem";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Signed program files grouped by signature result (for the report's trust summary).</summary>
    public IReadOnlyList<(SignatureState State, long Files)> GetSignatureStats()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sig_state, COUNT(*) FROM pe_info GROUP BY sig_state ORDER BY 2 DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<(SignatureState, long)>();
        while (r.Read()) list.Add(((SignatureState)r.GetInt32(0), r.GetInt64(1)));
        return list;
    }

    internal static long ToTicks(DateTime utc) => utc == default ? 0 : utc.Ticks;
    private static DateTime FromTicks(long ticks) => ticks <= 0 ? default : new DateTime(ticks, DateTimeKind.Utc);

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
