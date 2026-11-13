using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace PuterScanny.AI;

/// <summary>A GGUF model loaded into memory and run on this PC's processor with llama.cpp (through LLamaSharp). Nothing leaves the PC.</summary>
public sealed class LocalModel : IDisposable
{
    private static bool _nativeConfigured;
    private static readonly Lock NativeLock = new();

    private readonly LLamaWeights _weights;
    private readonly ModelParams _params;
    private readonly bool _disableThinking;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name { get; }

    private LocalModel(LLamaWeights weights, ModelParams parameters, string name)
    {
        _weights = weights;
        _params = parameters;
        Name = name;
        // Models with a "thinking" mode (like Qwen3.5) write long hidden reasoning first. An empty thinking block
        // switches that off, which is what their own chat templates do when thinking is disabled.
        _disableThinking = weights.Metadata.TryGetValue("tokenizer.chat_template", out var template) && template.Contains("<think>");
    }

    /// <summary>Loads a model file. <paramref name="threads"/> = processor threads to use (fewer = gentler on the PC).</summary>
    public static async Task<LocalModel> LoadAsync(string path, string name, int threads, IProgress<float>? progress, CancellationToken ct)
    {
        ConfigureNative();
        var parameters = new ModelParams(path)
        {
            ContextSize = 4096,
            BatchSize = 512,
            Threads = threads,
            BatchThreads = threads,
            GpuLayerCount = 0,
        };
        var weights = await LLamaWeights.LoadFromFileAsync(parameters, ct, progress);
        return new LocalModel(weights, parameters, name);
    }

    public static int SuggestedThreads(bool gentle)
    {
        int logical = Environment.ProcessorCount;
        return gentle ? Math.Clamp(logical / 4, 2, 8) : Math.Clamp(logical / 2, 2, 16);
    }

    private static void ConfigureNative()
    {
        lock (NativeLock)
        {
            if (_nativeConfigured) return;
            _nativeConfigured = true;
            // Quiet: llama.cpp otherwise prints hundreds of loading lines.
            NativeLibraryConfig.All.WithLogCallback((level, message) => { });
        }
    }

    /// <summary>Asks the model one question (system instructions + user message). Returns the raw answer.</summary>
    public async Task<string> GenerateAsync(string system, string user, int maxTokens, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var template = new LLamaTemplate(_weights) { AddAssistant = true };
            template.Add("system", system);
            template.Add("user", user);
            string prompt = Encoding.UTF8.GetString(template.Apply());
            if (_disableThinking && !prompt.TrimEnd().EndsWith("</think>", StringComparison.Ordinal)) prompt += "<think>\n\n</think>\n\n";

            var executor = new StatelessExecutor(_weights, _params) { ApplyTemplate = false };
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>", "<end_of_turn>", "\n\n\n"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, TopP = 0.9f, RepeatPenalty = 1.05f, Seed = 42 },
            };
            var sb = new StringBuilder();
            await foreach (string piece in executor.InferAsync(prompt, inference, ct)) sb.Append(piece);
            return sb.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Waits for an answer in progress to finish, then frees the model's memory.</summary>
    public void Dispose()
    {
        _gate.Wait();
        _weights.Dispose();
    }
}
