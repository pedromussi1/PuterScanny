using Microsoft.Data.Sqlite;

namespace PuterScanny.AI;

/// <summary>One AI-written explanation.</summary>
public sealed record Explanation(string Text, string ModelName, DateTime CreatedUtc, bool FromCache);

/// <summary>
/// Explanations already written, kept on this PC so each item is only explained once (a model answer can take
/// half a minute). Keyed by model + facts: if anything about the item changes, it's explained again.
/// </summary>
public sealed class ExplanationCache : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public ExplanationCache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS explanation (
                key         TEXT PRIMARY KEY,
                model       TEXT NOT NULL,
                path        TEXT,
                text        TEXT NOT NULL,
                created_utc INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_explanation_path ON explanation(path);
            """;
        cmd.ExecuteNonQuery();
    }

    public Explanation? Get(string key)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT text, model, created_utc FROM explanation WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new Explanation(r.GetString(0), r.GetString(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc), FromCache: true) : null;
        }
    }

    /// <summary>The most recent explanation written for this path by any model (for reports).</summary>
    public Explanation? GetLatestForPath(string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT text, model, created_utc FROM explanation WHERE path = $p COLLATE NOCASE ORDER BY created_utc DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new Explanation(r.GetString(0), r.GetString(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc), FromCache: true) : null;
        }
    }

    public void Put(string key, string? path, Explanation e)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO explanation (key, model, path, text, created_utc) VALUES ($k, $m, $p, $t, $c)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$m", e.ModelName);
            cmd.Parameters.AddWithValue("$p", (object?)path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", e.Text);
            cmd.Parameters.AddWithValue("$c", e.CreatedUtc.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    public long Count()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM explanation";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM explanation";
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();
}
