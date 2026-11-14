using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PuterScanny.Core.Models;

namespace PuterScanny.Storage;

/// <summary>Flexible read-only lookups (used by "Ask about this scan").</summary>
public sealed partial class ScanDatabase
{
    private const string UnidentifiedCondition =
        "(a.owner_app_id IS NULL AND a.location_rule IS NULL AND (e.is_dir = 1 OR IFNULL(a.category, 'other') = 'other'))";

    /// <summary>The scanned item at exactly this path (any case), or null.</summary>
    public FileSystemEntry? GetEntryByPath(string path)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{EntrySelect} WHERE e.path = $p COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$p", path.Length > 3 ? path.TrimEnd('\\') : path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntry(r) : null;
    }

    /// <summary>Every distinct folder name in the scan.</summary>
    public IReadOnlyList<string> GetFolderNames()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT name FROM entry WHERE is_dir = 1";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Items whose name matches, best match first: exact names, then names starting with the text, then the rest; shallower and bigger first.</summary>
    public IReadOnlyList<FileSystemEntry> FindByName(string text, string? scope, EntryKind kind, int limit)
    {
        using var cmd = _connection.CreateCommand();
        string scopeSql = ScopeClause(cmd, scope);
        cmd.CommandText = $"""
            {EntrySelect}
            WHERE e.name LIKE $pat ESCAPE '\' {scopeSql} {KindClause(kind)}
              AND e.id NOT IN (SELECT entry_id FROM removed)
            ORDER BY CASE WHEN e.name = $text COLLATE NOCASE THEN 0 WHEN e.name LIKE $start ESCAPE '\' THEN 1 ELSE 2 END,
                     e.depth, e.total_size DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$pat", "%" + EscapeLike(text) + "%");
        cmd.Parameters.AddWithValue("$start", EscapeLike(text) + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(cmd);
    }

    public IReadOnlyList<FileSystemEntry> QueryEntries(EntryQuery q)
    {
        using var cmd = _connection.CreateCommand();
        string where = BuildWhere(cmd, q, forListing: true);
        string order = q.Sort switch
        {
            EntrySort.Oldest => "MAX(COALESCE(a.contained_last_modified, e.modified_utc), e.created_utc) ASC, e.total_size DESC",
            EntrySort.Newest => "MAX(COALESCE(a.contained_last_modified, e.modified_utc), e.created_utc) DESC, e.total_size DESC",
            EntrySort.LeastRecentlyUsed => "COALESCE(a.last_used, a.contained_last_used, 0) ASC, e.total_size DESC",
            _ => "e.total_size DESC, e.name",
        };
        cmd.CommandText = $"""
            {EntrySelect}
            LEFT JOIN analysis pa ON pa.entry_id = e.parent_id
            WHERE {where}
            ORDER BY {order} LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, q.Limit));
        return ReadEntries(cmd);
    }

    /// <summary>
    /// How many files match and their total size. Only files are counted (never a folder and its contents twice),
    /// the same way the Overview and Clean up totals are made.
    /// </summary>
    public (long Files, long Bytes) SummarizeFiles(EntryQuery q)
    {
        using var cmd = _connection.CreateCommand();
        string where = BuildWhere(cmd, q with { Kind = EntryKind.File, TopmostOnly = false }, forListing: false);
        cmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM(e.size), 0) FROM entry e LEFT JOIN analysis a ON a.entry_id = e.id WHERE {where}";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    /// <summary>
    /// How many places match and their total size, counting a matching folder once with everything in it (for
    /// findings made about folders, such as mod downloads).
    /// </summary>
    public (long Items, long Bytes) SummarizeTopmost(EntryQuery q)
    {
        using var cmd = _connection.CreateCommand();
        string where = BuildWhere(cmd, q with { TopmostOnly = true }, forListing: true);
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(e.total_size), 0)
            FROM entry e LEFT JOIN analysis a ON a.entry_id = e.id LEFT JOIN analysis pa ON pa.entry_id = e.parent_id
            WHERE {where}
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    /// <summary>Files per recommendation inside a folder (or the whole scan), with their total size.</summary>
    public IReadOnlyList<(Verdict Verdict, CleanupAction Action, bool NoEvidence, long Files, long Bytes)> GetVerdictStats(string? scope)
    {
        using var cmd = _connection.CreateCommand();
        string scopeSql = ScopeClause(cmd, scope);
        cmd.CommandText = $"""
            SELECT a.verdict, a.action, (a.fact_flags & {(int)FactFlags.NoEvidence}) <> 0, COUNT(*), COALESCE(SUM(e.size), 0)
            FROM analysis a JOIN entry e ON e.id = a.entry_id
            WHERE e.is_dir = 0 AND a.verdict IS NOT NULL {scopeSql}
              AND e.id NOT IN (SELECT entry_id FROM removed)
            GROUP BY 1, 2, 3 ORDER BY 1 DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<(Verdict, CleanupAction, bool, long, long)>();
        while (r.Read()) list.Add(((Verdict)r.GetInt32(0), (CleanupAction)r.GetInt32(1), r.GetInt64(2) != 0, r.GetInt64(3), r.GetInt64(4)));
        return list;
    }

    /// <summary>Files per kind (knowledge-base category) inside a folder (or the whole scan), biggest first.</summary>
    public IReadOnlyList<CategoryStat> GetCategoryStats(string? scope)
    {
        using var cmd = _connection.CreateCommand();
        string scopeSql = ScopeClause(cmd, scope);
        cmd.CommandText = $"""
            SELECT COALESCE(a.category, 'other'), COUNT(*), COALESCE(SUM(e.size), 0)
            FROM entry e LEFT JOIN analysis a ON a.entry_id = e.id
            WHERE e.is_dir = 0 {scopeSql}
            GROUP BY 1 ORDER BY 3 DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<CategoryStat>();
        while (r.Read()) list.Add(new CategoryStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    /// <summary>Files per kind (as PuterScanny labels them, e.g. "Game video (Bink)") inside a folder, biggest first.</summary>
    public IReadOnlyList<TypeStat> GetTypeStats(string scope, int limit = 8)
    {
        using var cmd = _connection.CreateCommand();
        string scopeSql = ScopeClause(cmd, scope);
        cmd.CommandText = $"""
            SELECT COALESCE(NULLIF(a.type_label, ''), ''), MAX(COALESCE(a.category, 'other')), COUNT(*), COALESCE(SUM(e.size), 0)
            FROM entry e LEFT JOIN analysis a ON a.entry_id = e.id
            WHERE e.is_dir = 0 {scopeSql} AND e.id NOT IN (SELECT entry_id FROM removed)
            GROUP BY 1 ORDER BY 4 DESC, 3 DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<TypeStat>();
        while (r.Read()) list.Add(new TypeStat(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    /// <summary>The biggest files of one kind (see <see cref="GetTypeStats"/>) inside a folder.</summary>
    public IReadOnlyList<FileSystemEntry> GetFilesOfType(string scope, string typeLabel, int limit)
    {
        using var cmd = _connection.CreateCommand();
        string scopeSql = ScopeClause(cmd, scope);
        cmd.CommandText = $"""
            {EntrySelect}
            WHERE e.is_dir = 0 {scopeSql} AND COALESCE(NULLIF(a.type_label, ''), '') = $label
              AND e.id NOT IN (SELECT entry_id FROM removed)
            ORDER BY e.size DESC, e.name LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$label", typeLabel);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(cmd);
    }

    /// <summary>Extra copies of duplicate files inside the folder (never the copy to keep), biggest first.</summary>
    public IReadOnlyList<FileSystemEntry> GetDuplicateCopies(string? scope, int limit)
    {
        using var cmd = _connection.CreateCommand();
        string scopeSql = ScopeClause(cmd, scope);
        cmd.CommandText = $"""
            {EntrySelect}
            JOIN duplicate d ON d.entry_id = e.id
            WHERE d.is_keeper = 0 {scopeSql} AND e.id NOT IN (SELECT entry_id FROM removed)
            ORDER BY e.size DESC, e.path LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(cmd);
    }

    /// <summary>Sets of identical files with an extra copy inside the folder, how many extra copies, and the space they take.</summary>
    public (long Sets, long Copies, long Bytes) GetDuplicateStats(string? scope)
    {
        using var cmd = _connection.CreateCommand();
        string scopeSql = ScopeClause(cmd, scope);
        cmd.CommandText = $"""
            SELECT COUNT(DISTINCT d.group_id), COUNT(*), COALESCE(SUM(e.size), 0)
            FROM duplicate d JOIN entry e ON e.id = d.entry_id
            WHERE d.is_keeper = 0 {scopeSql} AND e.id NOT IN (SELECT entry_id FROM removed)
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    // ---------------------------------------------------------------- SQL building

    private static string BuildWhere(SqliteCommand cmd, EntryQuery q, bool forListing)
    {
        var sb = new StringBuilder("e.depth >= 0");
        sb.Append(ScopeClause(cmd, q.Scope));
        // The folder being asked about isn't one of its own contents.
        if (q.Scope is not null)
        {
            sb.Append(" AND e.path <> $scope COLLATE NOCASE");
        }
        else
        {
            sb.Append(" AND e.depth > 0");
        }
        sb.Append(KindClause(q.Kind));
        sb.Append(" AND e.id NOT IN (SELECT entry_id FROM removed)");

        if (q.Verdicts is { Count: > 0 } verdicts)
        {
            string list = string.Join(",", verdicts.Select(v => ((int)v).ToString(CultureInfo.InvariantCulture)));
            sb.Append($" AND a.verdict IN ({list})");
            if (forListing && q.TopmostOnly) sb.Append($" AND (pa.verdict IS NULL OR pa.verdict NOT IN ({list}))");
        }
        if (q.NoEvidence is { } noEvidence)
        {
            int flag = (int)FactFlags.NoEvidence;
            sb.Append(noEvidence
                ? $" AND a.verdict = {(int)Verdict.Keep} AND (a.fact_flags & {flag}) <> 0"
                : $" AND (a.verdict IS NULL OR a.verdict <> {(int)Verdict.Keep} OR (a.fact_flags & {flag}) = 0)");
        }
        if (q.AnyFacts != FactFlags.None)
        {
            int flags = (int)q.AnyFacts;
            sb.Append($" AND (a.fact_flags & {flags}) <> 0");
            if (forListing && q.TopmostOnly) sb.Append($" AND (COALESCE(pa.fact_flags, 0) & {flags}) = 0");
        }
        if (q.Unidentified)
        {
            sb.Append(" AND a.entry_id IS NOT NULL AND ").Append(UnidentifiedCondition);
            if (forListing && q.TopmostOnly)
                sb.Append(" AND NOT (e.depth > 1 AND pa.entry_id IS NOT NULL AND pa.owner_app_id IS NULL AND pa.location_rule IS NULL)");
        }
        if (q.Categories is { Count: > 0 } cats)
        {
            var names = new List<string>();
            int i = 0;
            foreach (string c in cats)
            {
                string p = "$cat" + i++.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(p, c);
                names.Add(p);
            }
            sb.Append($" AND e.is_dir = 0 AND COALESCE(a.category, 'other') IN ({string.Join(",", names)})");
        }
        if (!string.IsNullOrWhiteSpace(q.NameContains))
        {
            sb.Append(" AND e.name LIKE $name ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$name", "%" + EscapeLike(q.NameContains.Trim()) + "%");
        }
        if (q.NotUsedSinceUtc is { } since)
        {
            sb.Append(" AND COALESCE(a.last_used, a.contained_last_used, 0) < $since AND MAX(COALESCE(a.contained_last_modified, e.modified_utc), e.created_utc) < $since");
            cmd.Parameters.AddWithValue("$since", since.Ticks);
        }
        if (q.ModifiedBeforeUtc is { } before)
        {
            sb.Append(" AND MAX(COALESCE(a.contained_last_modified, e.modified_utc), e.created_utc) < $before");
            cmd.Parameters.AddWithValue("$before", before.Ticks);
        }
        if (q.MinBytes > 0)
        {
            sb.Append(" AND e.total_size >= $min");
            cmd.Parameters.AddWithValue("$min", q.MinBytes);
        }
        return sb.ToString();
    }

    private static string KindClause(EntryKind kind) => kind switch
    {
        EntryKind.File => " AND e.is_dir = 0",
        EntryKind.Folder => " AND e.is_dir = 1",
        _ => "",
    };

    /// <summary>"Inside this folder (or the folder itself)", without being fooled by similar names (D:\Games vs D:\Games2).</summary>
    private static string ScopeClause(SqliteCommand cmd, string? scope)
    {
        if (string.IsNullOrEmpty(scope)) return "";
        string folder = scope.Length > 3 ? scope.TrimEnd('\\') : scope;
        string prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        if (!cmd.Parameters.Contains("$scope"))
        {
            cmd.Parameters.AddWithValue("$scope", folder);
            cmd.Parameters.AddWithValue("$sprefix", prefix);
            cmd.Parameters.AddWithValue("$slen", prefix.Length);
        }
        return " AND (e.path = $scope COLLATE NOCASE OR substr(e.path, 1, $slen) = $sprefix COLLATE NOCASE)";
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
