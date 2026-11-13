using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuterScanny.Cleanup;

/// <summary>
/// The record of every clean-up (what was removed, how, and where it is now), saved after every single item so an
/// undo is possible even if the app or the PC stops halfway.
/// </summary>
public sealed class CleanupHistory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;
    private readonly object _lock = new();
    private List<CleanupBatch> _batches;

    public CleanupHistory(string? file = null)
    {
        _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuterScanny", "cleanup-history.json");
        _batches = Load(_file);
    }

    public string FilePath => _file;

    public IReadOnlyList<CleanupBatch> Batches
    {
        get { lock (_lock) return _batches.OrderByDescending(b => b.StartedUtc).ToList(); }
    }

    public CleanupBatch? Find(string batchId)
    {
        lock (_lock) return _batches.FirstOrDefault(b => b.Id == batchId);
    }

    public void Add(CleanupBatch batch)
    {
        lock (_lock)
        {
            _batches.Add(batch);
            Save();
        }
    }

    /// <summary>Writes the whole history atomically (write to a temp file, then replace).</summary>
    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_batches, Json));
            File.Move(tmp, _file, overwrite: true);
        }
    }

    private static List<CleanupBatch> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<CleanupBatch>>(File.ReadAllText(file), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            // Never lose the record of what can be restored: keep the damaged file aside and start a new one.
            File.Copy(file, file + $".damaged-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return [];
        }
    }
}
