namespace PuterScanny.AI;

public enum AiStatus
{
    /// <summary>Turned off, or no model downloaded yet.</summary>
    NotSetUp,

    /// <summary>Set up; the model loads on first use.</summary>
    Ready,

    Loading,
    Loaded,

    /// <summary>The model couldn't be loaded (see <see cref="ExplanationService.Problem"/>).</summary>
    Failed,
}

/// <summary>
/// PuterScanny's local AI: plain-English explanations for items the rules couldn't identify. It only ever describes;
/// it never changes a verdict, a protection or what can be cleaned up.
/// </summary>
public sealed class ExplanationService : IDisposable
{
    private readonly AiPaths _paths;
    private readonly Lazy<ExplanationCache> _cache;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private LocalModel? _model;
    private string? _loadedPath;

    /// <summary>For tests: answers without a real model.</summary>
    private readonly Func<string, string, CancellationToken, Task<string>>? _fakeGenerate;

    public ExplanationService(AiPaths paths, Func<string, string, CancellationToken, Task<string>>? fakeGenerate = null)
    {
        _paths = paths;
        _fakeGenerate = fakeGenerate;
        Settings = AiSettings.Load(paths);
        _cache = new Lazy<ExplanationCache>(() => new ExplanationCache(paths.Cache));
    }

    public AiPaths Paths => _paths;
    public AiSettings Settings { get; }
    public string? Problem { get; private set; }
    public bool GentleOnPc { get; set; } = true;

    public event EventHandler? StatusChanged;

    public AiStatus Status
    {
        get
        {
            if (_fakeGenerate is not null) return Settings.Enabled ? AiStatus.Loaded : AiStatus.NotSetUp;
            if (!Settings.Enabled || ResolveModel() is null) return AiStatus.NotSetUp;
            if (Problem is not null) return AiStatus.Failed;
            if (_model is not null) return AiStatus.Loaded;
            return _loadGate.CurrentCount == 0 ? AiStatus.Loading : AiStatus.Ready;
        }
    }

    public bool IsAvailable => Status is AiStatus.Ready or AiStatus.Loading or AiStatus.Loaded;

    /// <summary>The model file to use and its display name, or null when none is ready.</summary>
    public (string Path, string Name, string Id)? ResolveModel()
    {
        if (!string.IsNullOrWhiteSpace(Settings.CustomModelPath))
        {
            return File.Exists(Settings.CustomModelPath)
                ? (Settings.CustomModelPath, System.IO.Path.GetFileNameWithoutExtension(Settings.CustomModelPath), "custom:" + System.IO.Path.GetFileName(Settings.CustomModelPath))
                : null;
        }
        var m = ModelCatalog.Find(Settings.ModelId) ?? ModelCatalog.Default;
        return ModelDownloader.GetState(m, _paths.Models) == ModelState.Ready
            ? (ModelDownloader.FinalPath(m, _paths.Models), m.DisplayName.Split(" (")[0], m.Id)
            : null;
    }

    public void SaveSettings()
    {
        Settings.Save(_paths);
        if (_loadedPath is not null && ResolveModel()?.Path != _loadedPath) Unload();
        Problem = null;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An explanation written earlier for exactly these facts (no model needed).</summary>
    public Explanation? GetCached(ExplainFacts facts)
    {
        var model = ResolveModel();
        if (model is null && _fakeGenerate is null) return null;
        return _cache.Value.Get(ExplanationPrompt.CacheKey(model?.Id ?? "fake", facts));
    }

    /// <summary>The newest explanation for a path, from any model and any scan (for reports).</summary>
    public Explanation? GetLatestForPath(string path)
    {
        try { return File.Exists(_paths.Cache) ? _cache.Value.GetLatestForPath(path) : null; }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    public long CachedCount => File.Exists(_paths.Cache) ? _cache.Value.Count() : 0;

    public void ClearCache() => _cache.Value.Clear();

    /// <summary>
    /// Explains an item: from the cache when possible, otherwise by asking the model. Returns null when the model
    /// gave no usable answer (it's never shown half-made or with keep/delete advice).
    /// </summary>
    public async Task<Explanation?> ExplainAsync(ExplainFacts facts, string? path, CancellationToken ct, IProgress<float>? loadProgress = null)
    {
        var resolved = ResolveModel();
        if (resolved is null && _fakeGenerate is null) throw new InvalidOperationException("The local AI isn't set up yet.");
        string modelId = resolved?.Id ?? "fake";
        string modelName = resolved?.Name ?? "Test model";
        string key = ExplanationPrompt.CacheKey(modelId, facts);
        if (_cache.Value.Get(key) is { } cached) return cached;

        string user = ExplanationPrompt.BuildUserMessage(facts);
        string raw;
        if (_fakeGenerate is not null)
        {
            raw = await _fakeGenerate(ExplanationPrompt.System, user, ct);
        }
        else
        {
            var model = await EnsureLoadedAsync(resolved!.Value.Path, modelName, loadProgress, ct);
            raw = await Task.Run(() => model.GenerateAsync(ExplanationPrompt.System, user, 220, ct), ct);
        }

        string? text = ExplanationCleaner.Clean(raw);
        if (text is null) return null;
        var result = new Explanation(text, modelName, DateTime.UtcNow, FromCache: false);
        _cache.Value.Put(key, path, result);
        return result;
    }

    /// <summary>
    /// Runs the local model on any prompt (used by "Ask about this scan"). Loads the model first when needed.
    /// </summary>
    public async Task<string> GenerateAsync(string system, string user, int maxTokens, string? grammar, float temperature, CancellationToken ct, IProgress<float>? loadProgress = null)
    {
        if (_fakeGenerate is not null) return await _fakeGenerate(system, user, ct);
        var resolved = ResolveModel() ?? throw new InvalidOperationException("The local AI isn't set up yet.");
        var model = await EnsureLoadedAsync(resolved.Path, resolved.Name, loadProgress, ct);
        return await Task.Run(() => model.GenerateAsync(system, user, maxTokens, ct, grammar, temperature), ct);
    }

    private async Task<LocalModel> EnsureLoadedAsync(string path, string name, IProgress<float>? progress, CancellationToken ct)
    {
        if (_model is not null && _loadedPath == path) return _model;
        await _loadGate.WaitAsync(ct);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            if (_model is not null && _loadedPath == path) return _model;
            ReleaseModel();
            try
            {
                _model = await Task.Run(() => LocalModel.LoadAsync(path, name, LocalModel.SuggestedThreads(GentleOnPc), progress, ct), ct);
                _loadedPath = path;
                Problem = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Problem = $"The AI model couldn't be loaded: {ex.Message}";
                throw new InvalidOperationException(Problem, ex);
            }
            return _model;
        }
        finally
        {
            _loadGate.Release();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Frees the memory the model uses (it's loaded again when next needed).</summary>
    public void Unload()
    {
        ReleaseModel();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseModel()
    {
        var old = _model;
        _model = null;
        _loadedPath = null;
        if (old is not null) Task.Run(old.Dispose); // waits for an answer in progress, off the UI thread
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        if (_cache.IsValueCreated) _cache.Value.Dispose();
    }
}
