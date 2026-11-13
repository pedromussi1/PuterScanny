# Third-party notices

PuterScanny is MIT-licensed. It is built on, and ships with, the following free software. Each keeps its own licence.

| Component | Used for | Licence |
|---|---|---|
| [.NET 10 runtime and WPF](https://github.com/dotnet) (Microsoft) | Runs the app; the release includes the runtime so nothing else needs installing | MIT |
| [CommunityToolkit.Mvvm and CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) (.NET Foundation) | App structure | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) (Microsoft) | Saving scans | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (Eric Sink) | SQLite access | Apache-2.0 |
| [SQLite](https://sqlite.org) | Scan database engine | Public domain |
| [LLamaSharp and LLamaSharp.Backend.Cpu](https://github.com/SciSharp/LLamaSharp) (SciSharp) | Running the optional local AI | MIT |
| [llama.cpp and ggml](https://github.com/ggml-org/llama.cpp) (the ggml authors), included in LLamaSharp.Backend.Cpu | The AI engine | MIT |
| Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Bcl.AsyncInterfaces, Microsoft.Bcl.Memory, System.Numerics.Tensors (Microsoft) | Used by LLamaSharp | MIT |
| [System.Linq.Async and System.Interactive.Async](https://github.com/dotnet/reactive) (.NET Foundation) | Used by LLamaSharp | MIT |

## Downloaded on request, not included

The local AI model is **not** part of the download. It is fetched from Hugging Face only when you press "Download" in the app's Local AI window, and checked against a fixed SHA-256 fingerprint before it's used.

| Model | Made by | Licence |
|---|---|---|
| [Qwen3.5-4B](https://huggingface.co/Qwen) (GGUF packaging: [unsloth/Qwen3.5-4B-GGUF](https://huggingface.co/unsloth/Qwen3.5-4B-GGUF)) | Alibaba Qwen team | Apache-2.0 |
| [Qwen3.5-2B](https://huggingface.co/Qwen) (GGUF packaging: [unsloth/Qwen3.5-2B-GGUF](https://huggingface.co/unsloth/Qwen3.5-2B-GGUF)) | Alibaba Qwen team | Apache-2.0 |

## Built with

[Inno Setup](https://jrsoftware.org/isinfo.php) (Jordan Russell and Martijn Laan) makes the installer. Its licence allows free use for any purpose, including commercial.
