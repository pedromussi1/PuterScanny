using Microsoft.Data.Sqlite;
using PuterScanny.Core.Models;

namespace PuterScanny.Storage;

/// <summary>Writes analysis results (programs, identity, ownership) in large batched transactions.</summary>
public sealed class AnalysisWriter : IDisposable
{
    private const int BatchSize = 20_000;

    private readonly ScanDatabase _db;
    private readonly SqliteConnection _conn;
    private SqliteTransaction? _tx;
    private int _pending;
    private SqliteCommand? _insertAnalysis;
    private SqliteCommand? _updateIdentity;
    private SqliteCommand? _insertPe;
    private SqliteCommand? _insertReference;
    private SqliteCommand? _insertUsage;
    private SqliteCommand? _setRecommendation;
    private SqliteCommand? _setFolderTotals;

    internal AnalysisWriter(ScanDatabase db)
    {
        _db = db;
        _conn = db.Connection;
        // Crash-safe (see SqliteScanSink): an interrupted analysis can be resumed from the last saved batch.
        _db.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        BeginBatch();
    }

    public void WriteApps(IEnumerable<InstalledApp> apps)
    {
        using var cmd = Command("""
            INSERT INTO app (id, name, publisher, version, install_location, install_date, estimated_size, source, uninstall_command,
                is_system_component, is_framework, source_key, extra_folders, owned_file_count)
            VALUES ($id, $name, $pub, $ver, $loc, $date, $size, $src, $un, $sys, $fw, $key, $extra, $owned)
            """);
        foreach (var a in apps)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", a.Id);
            cmd.Parameters.AddWithValue("$name", a.Name);
            cmd.Parameters.AddWithValue("$pub", (object?)a.Publisher ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", (object?)a.Version ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$loc", (object?)a.InstallLocation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$date", a.InstallDate is { } d ? d.Ticks : DBNull.Value);
            cmd.Parameters.AddWithValue("$size", (object?)a.EstimatedSizeBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", (int)a.Source);
            cmd.Parameters.AddWithValue("$un", (object?)a.UninstallCommand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sys", a.IsSystemComponent ? 1 : 0);
            cmd.Parameters.AddWithValue("$fw", a.IsFramework ? 1 : 0);
            cmd.Parameters.AddWithValue("$key", (object?)a.SourceKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$extra", a.ExtraFolders.Count == 0 ? DBNull.Value : string.Join('|', a.ExtraFolders));
            cmd.Parameters.AddWithValue("$owned", a.OwnedFiles.Count > 0 ? a.OwnedFiles.Count : a.OwnedFileCount);
            cmd.ExecuteNonQuery();
        }
    }

    public void WriteInventoryProblems(IEnumerable<string> problems)
    {
        using var cmd = Command("INSERT INTO inventory_problem (message) VALUES ($m)");
        foreach (var p in problems)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$m", p);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddAnalysis(long entryId, long? ownerAppId, bool ownerExact, string? locationRule, string? category, string? typeLabel,
        Protection protection = Protection.None, UsageSummary? usage = null)
    {
        var p = _insertAnalysis!.Parameters;
        p["$id"].Value = entryId;
        p["$owner"].Value = ownerAppId is long o ? o : DBNull.Value;
        p["$exact"].Value = ownerExact ? 1 : 0;
        p["$rule"].Value = (object?)locationRule ?? DBNull.Value;
        p["$cat"].Value = (object?)category ?? DBNull.Value;
        p["$label"].Value = (object?)typeLabel ?? DBNull.Value;
        p["$prot"].Value = (int)protection;
        p["$used"].Value = usage?.LastUsedUtc is { } lu ? lu.Ticks : DBNull.Value;
        p["$count"].Value = usage?.UseCount ?? 0;
        p["$active"].Value = (long)(usage?.ActiveTime.TotalSeconds ?? 0);
        _insertAnalysis.ExecuteNonQuery();
        Tick();
    }

    public void AddReference(long entryId, SystemReference reference)
    {
        var p = _insertReference!.Parameters;
        p["$id"].Value = entryId;
        p["$kind"].Value = (int)reference.Kind;
        p["$level"].Value = (int)reference.Level;
        p["$detail"].Value = reference.Detail;
        _insertReference.ExecuteNonQuery();
        Tick();
    }

    public void AddUsage(long entryId, UsageEvent e)
    {
        var p = _insertUsage!.Parameters;
        p["$id"].Value = entryId;
        p["$src"].Value = (int)e.Source;
        p["$used"].Value = e.LastUsedUtc is { } t ? t.Ticks : DBNull.Value;
        p["$count"].Value = e.Count is int c ? c : DBNull.Value;
        p["$active"].Value = e.ActiveTime is { } a ? (long)a.TotalSeconds : DBNull.Value;
        p["$detail"].Value = e.Detail;
        _insertUsage.ExecuteNonQuery();
        Tick();
    }

    public void AddAppUsage(long appId, UsageEvent e)
    {
        using var cmd = Command("INSERT INTO app_usage (app_id, source, last_used, count, active_seconds, detail) VALUES ($id, $src, $used, $count, $active, $detail)");
        cmd.Parameters.AddWithValue("$id", appId);
        cmd.Parameters.AddWithValue("$src", (int)e.Source);
        cmd.Parameters.AddWithValue("$used", e.LastUsedUtc is { } t ? t.Ticks : DBNull.Value);
        cmd.Parameters.AddWithValue("$count", e.Count is int c ? c : DBNull.Value);
        cmd.Parameters.AddWithValue("$active", e.ActiveTime is { } a ? (long)a.TotalSeconds : DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", e.Detail);
        cmd.ExecuteNonQuery();
    }

    public void SetAppUsage(IEnumerable<(long AppId, UsageSummary Usage)> values)
    {
        using var cmd = Command("UPDATE app SET last_used = $used, use_count = $count, active_seconds = $active WHERE id = $id");
        foreach (var (appId, u) in values)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.Parameters.AddWithValue("$used", u.LastUsedUtc is { } t ? t.Ticks : DBNull.Value);
            cmd.Parameters.AddWithValue("$count", u.UseCount);
            cmd.Parameters.AddWithValue("$active", (long)u.ActiveTime.TotalSeconds);
            cmd.ExecuteNonQuery();
        }
    }

    public void WriteUsageSources(IEnumerable<UsageSourceStatus> sources)
    {
        using var cmd = Command("INSERT OR REPLACE INTO usage_source (source, available, items, note) VALUES ($s, $a, $i, $n)");
        foreach (var s in sources)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$s", (int)s.Source);
            cmd.Parameters.AddWithValue("$a", s.Available ? 1 : 0);
            cmd.Parameters.AddWithValue("$i", s.Items);
            cmd.Parameters.AddWithValue("$n", s.Note);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>For every folder, when anything inside it was last changed.</summary>
    public void SetContainedLastModified(IEnumerable<(long DirId, DateTime When)> values)
    {
        using var cmd = Command("UPDATE analysis SET contained_last_modified = $t WHERE entry_id = $id");
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        var t = cmd.Parameters.Add("$t", SqliteType.Integer);
        foreach (var (dirId, when) in values)
        {
            id.Value = dirId;
            t.Value = when.Ticks;
            cmd.ExecuteNonQuery();
        }
    }

    public void SetRecommendation(long entryId, Recommendation r, FactFlags facts)
    {
        _setRecommendation ??= Prepare("""
            UPDATE analysis SET verdict = $v, confidence = $c, action = $a, headline = $h, fact_flags = $f WHERE entry_id = $id
            """, "$id", "$v", "$c", "$a", "$h", "$f");
        _setRecommendation.Transaction = _tx;
        var p = _setRecommendation.Parameters;
        p["$id"].Value = entryId;
        p["$v"].Value = (int)r.Verdict;
        p["$c"].Value = r.Confidence;
        p["$a"].Value = (int)r.Action;
        p["$h"].Value = r.Headline;
        p["$f"].Value = (int)facts;
        _setRecommendation.ExecuteNonQuery();
        Tick();
    }

    public void SetFolderTotals(long dirId, long reclaim, long review, long tool, string? topReasons)
    {
        _setFolderTotals ??= Prepare("""
            UPDATE analysis SET reclaim_bytes = $r, review_bytes = $v, tool_bytes = $t, top_reasons = $top WHERE entry_id = $id
            """, "$id", "$r", "$v", "$t", "$top");
        _setFolderTotals.Transaction = _tx;
        var p = _setFolderTotals.Parameters;
        p["$id"].Value = dirId;
        p["$r"].Value = reclaim;
        p["$v"].Value = review;
        p["$t"].Value = tool;
        p["$top"].Value = (object?)topReasons ?? DBNull.Value;
        _setFolderTotals.ExecuteNonQuery();
        Tick();
    }

    public void AddDuplicate(long entryId, long groupId, bool isKeeper, int copies, string keeperPath)
    {
        using var cmd = Command("INSERT OR REPLACE INTO duplicate (entry_id, group_id, is_keeper, copies, keeper_path) VALUES ($id, $g, $k, $c, $p)");
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$k", isKeeper ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", copies);
        cmd.Parameters.AddWithValue("$p", keeperPath);
        cmd.ExecuteNonQuery();
        Tick();
    }

    public void ClearDuplicates()
    {
        using var cmd = Command("DELETE FROM duplicate");
        cmd.ExecuteNonQuery();
    }

    public void SetUninstallSuggestion(long appId, int confidence, string? reason)
    {
        using var cmd = Command("UPDATE app SET uninstall_confidence = $c, uninstall_reason = $r WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", appId);
        cmd.Parameters.AddWithValue("$c", confidence);
        cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>For every folder, when anything inside it was last used.</summary>
    public void SetContainedLastUsed(IEnumerable<(long DirId, DateTime LastUsed)> values)
    {
        using var cmd = Command("UPDATE analysis SET contained_last_used = $t WHERE entry_id = $id");
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        var t = cmd.Parameters.Add("$t", SqliteType.Integer);
        foreach (var (dirId, when) in values)
        {
            id.Value = dirId;
            t.Value = when.Ticks;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Stores, for every folder, the strongest protection of anything inside it.</summary>
    public void SetContainedProtection(IEnumerable<(long DirId, Protection Level)> values)
    {
        using var cmd = Command("UPDATE analysis SET contained_protection = $p WHERE entry_id = $id");
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        var level = cmd.Parameters.Add("$p", SqliteType.Integer);
        foreach (var (dirId, prot) in values)
        {
            id.Value = dirId;
            level.Value = (int)prot;
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateIdentity(ContentIdentity id, string? category, string? typeLabel)
    {
        var p = _updateIdentity!.Parameters;
        p["$id"].Value = id.EntryId;
        p["$cat"].Value = (object?)category ?? DBNull.Value;
        p["$label"].Value = (object?)typeLabel ?? DBNull.Value;
        p["$fmt"].Value = (object?)id.FormatId ?? DBNull.Value;
        p["$mm"].Value = id.ExtensionMismatch ? 1 : 0;
        p["$rs"].Value = (int)id.ReadStatus;
        // A file locked by a running program is in use right now, whatever else we know about it.
        p["$prot"].Value = id.ReadStatus == ReadStatus.InUse ? (int)Protection.InUse : 0;
        _updateIdentity.ExecuteNonQuery();
        if (id.ReadStatus == ReadStatus.InUse)
            AddReference(id.EntryId, new SystemReference(ReferenceKind.Locked, Protection.InUse, "Locked by a running program (could not even be opened for reading)"));

        if (id.Pe is { } pe)
        {
            var q = _insertPe!.Parameters;
            q["$id"].Value = id.EntryId;
            q["$machine"].Value = pe.Machine;
            q["$sub"].Value = pe.Subsystem;
            q["$dll"].Value = pe.IsDll ? 1 : 0;
            q["$drv"].Value = pe.IsDriver ? 1 : 0;
            q["$net"].Value = pe.IsDotNet ? 1 : 0;
            q["$link"].Value = pe.LinkTimeUtc is { } t ? t.Ticks : DBNull.Value;
            q["$company"].Value = (object?)pe.CompanyName ?? DBNull.Value;
            q["$product"].Value = (object?)pe.ProductName ?? DBNull.Value;
            q["$desc"].Value = (object?)pe.FileDescription ?? DBNull.Value;
            q["$fver"].Value = (object?)pe.FileVersion ?? DBNull.Value;
            q["$pver"].Value = (object?)pe.ProductVersion ?? DBNull.Value;
            q["$orig"].Value = (object?)pe.OriginalFilename ?? DBNull.Value;
            q["$internal"].Value = (object?)pe.InternalName ?? DBNull.Value;
            q["$copy"].Value = (object?)pe.LegalCopyright ?? DBNull.Value;
            q["$hasSig"].Value = pe.HasEmbeddedSignature ? 1 : 0;
            q["$state"].Value = (int)pe.SignatureState;
            q["$kind"].Value = (int)pe.SignatureKind;
            q["$by"].Value = (object?)pe.SignedBy ?? DBNull.Value;
            q["$err"].Value = pe.SignatureError;
            _insertPe.ExecuteNonQuery();
        }
        Tick();
    }

    public void WriteExtensionStats(IEnumerable<ExtensionStat> stats)
    {
        using var cmd = Command("""
            INSERT OR REPLACE INTO extension_info (ext, files, bytes, label, category, known, windows_type_name, opens_with)
            VALUES ($ext, $files, $bytes, $label, $cat, $known, $win, $open)
            """);
        foreach (var s in stats)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$ext", s.Extension);
            cmd.Parameters.AddWithValue("$files", s.Files);
            cmd.Parameters.AddWithValue("$bytes", s.Bytes);
            cmd.Parameters.AddWithValue("$label", (object?)s.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cat", (object?)s.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$known", s.Known ? 1 : 0);
            cmd.Parameters.AddWithValue("$win", (object?)s.WindowsTypeName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$open", (object?)s.OpensWith ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateAppMeasurements(IEnumerable<(long AppId, long Bytes, long Files)> totals)
    {
        using var cmd = Command("UPDATE app SET measured_bytes = $b, measured_files = $f WHERE id = $id");
        foreach (var (appId, bytes, files) in totals)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.Parameters.AddWithValue("$b", bytes);
            cmd.Parameters.AddWithValue("$f", files);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetMeta(string key, string? value)
    {
        using var cmd = Command("INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value");
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Commits everything written so far (call between stages so progress survives a cancel or crash).</summary>
    public void Flush()
    {
        CommitBatch();
        BeginBatch();
        _sinceFlush.Restart();
    }

    private readonly System.Diagnostics.Stopwatch _sinceFlush = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Commits if the last commit is older than <paramref name="interval"/>, so a crash loses only seconds of work.</summary>
    public void FlushIfOlderThan(TimeSpan interval)
    {
        if (_sinceFlush.Elapsed >= interval) Flush();
    }

    /// <summary>Removes earlier analysis results so the ownership stage can be redone from scratch.</summary>
    public void ResetAnalysis()
    {
        foreach (string table in new[] { "analysis", "pe_info", "app", "extension_info", "inventory_problem", "reference", "usage", "app_usage", "usage_source", "duplicate" })
        {
            using var cmd = Command($"DELETE FROM {table}");
            cmd.ExecuteNonQuery();
        }
        using var meta = Command("DELETE FROM meta WHERE key LIKE 'analysis.%'");
        meta.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        return cmd;
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

        _insertAnalysis ??= Prepare("""
            INSERT INTO analysis (entry_id, owner_app_id, owner_exact, location_rule, category, type_label, protection, last_used, use_count, active_seconds)
            VALUES ($id, $owner, $exact, $rule, $cat, $label, $prot, $used, $count, $active)
            """, "$id", "$owner", "$exact", "$rule", "$cat", "$label", "$prot", "$used", "$count", "$active");
        _insertUsage ??= Prepare("INSERT INTO usage (entry_id, source, last_used, count, active_seconds, detail) VALUES ($id, $src, $used, $count, $active, $detail)",
            "$id", "$src", "$used", "$count", "$active", "$detail");
        _insertReference ??= Prepare("INSERT INTO reference (entry_id, kind, level, detail) VALUES ($id, $kind, $level, $detail)",
            "$id", "$kind", "$level", "$detail");
        _updateIdentity ??= Prepare("""
            UPDATE analysis SET category = $cat, type_label = $label, format_id = $fmt, mismatch = $mm, read_status = $rs,
                protection = MAX(protection, $prot)
            WHERE entry_id = $id
            """, "$id", "$cat", "$label", "$fmt", "$mm", "$rs", "$prot");
        _insertPe ??= Prepare("""
            INSERT OR REPLACE INTO pe_info (entry_id, machine, subsystem, is_dll, is_driver, is_dotnet, link_time, company, product,
                description, file_version, product_version, original_filename, internal_name, copyright, has_embedded_sig,
                sig_state, sig_kind, signed_by, sig_error)
            VALUES ($id, $machine, $sub, $dll, $drv, $net, $link, $company, $product, $desc, $fver, $pver, $orig, $internal,
                $copy, $hasSig, $state, $kind, $by, $err)
            """, "$id", "$machine", "$sub", "$dll", "$drv", "$net", "$link", "$company", "$product", "$desc", "$fver", "$pver",
            "$orig", "$internal", "$copy", "$hasSig", "$state", "$kind", "$by", "$err");

        _insertAnalysis.Transaction = _tx;
        _insertReference.Transaction = _tx;
        _insertUsage.Transaction = _tx;
        _updateIdentity.Transaction = _tx;
        _insertPe.Transaction = _tx;
    }

    private SqliteCommand Prepare(string sql, params string[] names)
    {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach (string n in names) cmd.Parameters.Add(new SqliteParameter(n, DBNull.Value));
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

    public void Dispose()
    {
        CommitBatch();
        _insertAnalysis?.Dispose();
        _insertReference?.Dispose();
        _insertUsage?.Dispose();
        _setRecommendation?.Dispose();
        _setFolderTotals?.Dispose();
        _insertPe?.Dispose();
        _updateIdentity?.Dispose();
    }
}
