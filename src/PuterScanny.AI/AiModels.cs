using System.Text.Json;

namespace PuterScanny.AI;

/// <summary>A free, openly licensed language model PuterScanny can download and run offline.</summary>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    string FileName,
    string Url,
    long SizeBytes,
    string Sha256,
    string License,
    string Source,
    string Description,
    bool Recommended = false);

/// <summary>The models offered in the app. Every file is pinned by size and SHA-256, so a damaged or swapped download is refused.</summary>
public static class ModelCatalog
{
    public static readonly IReadOnlyList<ModelInfo> All =
    [
        new("qwen3.5-4b-q4km", "Qwen3.5 4B (recommended)", "Qwen3.5-4B-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf",
            2_740_937_888, "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
            "Apache-2.0", "Alibaba Qwen team, packaged by Unsloth on Hugging Face",
            "Good general knowledge of Windows and common programs. About 10 to 30 seconds per explanation on a typical PC.",
            Recommended: true),
        new("qwen3.5-2b-q4km", "Qwen3.5 2B (smaller, faster)", "Qwen3.5-2B-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf",
            1_280_835_840, "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
            "Apache-2.0", "Alibaba Qwen team, packaged by Unsloth on Hugging Face",
            "Half the size and about twice as fast, but more often vague or wrong. For older or low-memory PCs."),
    ];

    public static ModelInfo Default => All.First(m => m.Recommended);

    public static ModelInfo? Find(string? id) => All.FirstOrDefault(m => m.Id == id);
}

/// <summary>Where PuterScanny keeps its AI files. Everything stays on this PC.</summary>
public sealed class AiPaths
{
    public AiPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuterScanny");
    }

    public string Root { get; }
    public string Models => Path.Combine(Root, "Models");
    public string Settings => Path.Combine(Root, "ai-settings.json");
    public string Cache => Path.Combine(Root, "ai-explanations.db");
}

/// <summary>The user's local-AI choices.</summary>
public sealed class AiSettings
{
    /// <summary>
    /// Use the local AI automatically, only when it helps (on by default; nothing happens until a model is downloaded,
    /// which is the user's choice). False = never.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string ModelId { get; set; } = ModelCatalog.Default.Id;

    /// <summary>A GGUF file the user already has, used instead of a catalog model.</summary>
    public string? CustomModelPath { get; set; }

    /// <summary>Explain an unidentified item automatically when it is selected.</summary>
    public bool AutoExplain { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AiSettings Load(AiPaths paths)
    {
        try
        {
            if (File.Exists(paths.Settings))
                return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(paths.Settings)) ?? new AiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AiSettings();
    }

    public void Save(AiPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        string tmp = paths.Settings + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, paths.Settings, overwrite: true);
    }
}
