using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PuterScanny.AI;
using PuterScanny.Analysis;
using PuterScanny.Analysis.Reporting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

public sealed class ExplanationCleanerTests
{
    [Fact]
    public void Advice_sentences_are_removed_and_descriptions_kept()
    {
        string raw = "This file is created by Windows to save memory contents when the PC hibernates. " +
                     "Because it is a large system file, it is normal for it to exist, but it can be safely removed if you never hibernate. " +
                     "It is safe to delete. You should keep it. I recommend leaving it alone. " +
                     "It is rebuilt automatically when hibernation is turned on.";
        string? text = ExplanationCleaner.Clean(raw);
        Assert.NotNull(text);
        Assert.Contains("hibernates", text);
        Assert.Contains("rebuilt automatically", text);
        Assert.DoesNotContain("safely removed", text);
        Assert.DoesNotContain("safe to delete", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keep", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recommend", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("You can delete this folder.")]
    [InlineData("It's probably fine to remove it.")]
    [InlineData("This data is no longer needed by the program.")]
    [InlineData("Consider deleting it to free up space.")]
    [InlineData("Do not delete this file.")]
    [InlineData("It should not be removed.")]
    [InlineData("Deleting it would break the game.")]
    [InlineData("It may be safely deleted.")]
    public void Typical_advice_is_recognised(string sentence) => Assert.True(ExplanationCleaner.IsAdvice(sentence), sentence);

    [Theory]
    [InlineData("It stores shader programs compiled by the NVIDIA driver.")]
    [InlineData("The game removes old saves automatically when you start a new one.")]
    [InlineData("It is probably a settings file of a program called Zxqv.")]
    [InlineData("It is not clear what created this file.")]
    public void Plain_descriptions_are_not_advice(string sentence) => Assert.False(ExplanationCleaner.IsAdvice(sentence), sentence);

    [Fact]
    public void Thinking_markup_and_special_tokens_are_stripped()
    {
        string raw = "<think>The user wants… let me reason.</think>\n\n## Answer\n- **This folder** holds cached thumbnails, e.g. small previews of photos. It is used by Windows Explorer.<|im_end|>";
        string? text = ExplanationCleaner.Clean(raw);
        Assert.Equal("This folder holds cached thumbnails, e.g. small previews of photos. It is used by Windows Explorer.", text);
    }

    [Fact]
    public void Unfinished_thinking_or_advice_only_gives_no_answer()
    {
        Assert.Null(ExplanationCleaner.Clean("<think>Hmm, this could be"));
        Assert.Null(ExplanationCleaner.Clean("You can safely delete it."));
        Assert.Null(ExplanationCleaner.Clean("   "));
    }

    [Fact]
    public void Long_answers_are_capped()
    {
        string raw = string.Join(" ", Enumerable.Range(1, 12).Select(i => $"Sentence number {i} describes a detail of this cache folder."));
        string text = ExplanationCleaner.Clean(raw)!;
        Assert.True(text.Length <= ExplanationCleaner.MaxLength + 1);
        Assert.Contains("Sentence number 5 ", text);
        Assert.DoesNotContain("Sentence number 6 ", text);
    }
}

public sealed class ExplanationPromptTests
{
    private static ExplainFacts Facts(string size) => ExplainFacts.Create("data.bin", false, [("Location", @"C:\Games\Foo"), ("Size", size), ("Empty", null)]);

    [Fact]
    public void Message_lists_the_facts_and_skips_empty_ones()
    {
        string msg = ExplanationPrompt.BuildUserMessage(Facts("2 MB"));
        Assert.Contains("What is this file?", msg);
        Assert.Contains("Name: data.bin", msg);
        Assert.Contains(@"Location: C:\Games\Foo", msg);
        Assert.DoesNotContain("Empty", msg);
    }

    [Fact]
    public void Cache_key_changes_with_facts_and_model_only()
    {
        Assert.Equal(ExplanationPrompt.CacheKey("m", Facts("2 MB")), ExplanationPrompt.CacheKey("m", Facts("2 MB")));
        Assert.NotEqual(ExplanationPrompt.CacheKey("m", Facts("2 MB")), ExplanationPrompt.CacheKey("m", Facts("3 MB")));
        Assert.NotEqual(ExplanationPrompt.CacheKey("m", Facts("2 MB")), ExplanationPrompt.CacheKey("other", Facts("2 MB")));
    }

    [Theory]
    [InlineData(@"C:\Users\Alex\AppData\Local\Foo", @"C:\Users\<you>\AppData\Local\Foo")]
    [InlineData(@"c:\users\Alex", @"c:\users\<you>")]
    [InlineData(@"C:\Users\Public\Documents", @"C:\Users\Public\Documents")]
    [InlineData(@"D:\Games\Users\Alex", @"D:\Games\Users\Alex")]
    public void User_name_is_masked_in_paths(string path, string expected) => Assert.Equal(expected, ExplanationPrompt.MaskUserName(path));

    [Fact]
    public void Instructions_forbid_advice() =>
        Assert.Contains("Do not say whether to keep, delete", ExplanationPrompt.System);
}

public sealed class ExplanationServiceTests
{
    private static ExplainFacts Sample => ExplainFacts.Create("thing.dat", false, [("Location", @"D:\Stuff")]);

    [Fact]
    public async Task Answers_are_cleaned_cached_and_reused()
    {
        using var dir = new TempTree();
        int calls = 0;
        using var svc = new ExplanationService(new AiPaths(dir.Root), (system, user, ct) =>
        {
            calls++;
            Assert.Contains("Name: thing.dat", user);
            return Task.FromResult("This is a data file of a program called Stuff. You can delete it safely.");
        });
        svc.Settings.Enabled = true;

        var first = await svc.ExplainAsync(Sample, @"D:\Stuff\thing.dat", default);
        Assert.NotNull(first);
        Assert.False(first.FromCache);
        Assert.Equal("This is a data file of a program called Stuff.", first.Text);

        var second = await svc.ExplainAsync(Sample, @"D:\Stuff\thing.dat", default);
        Assert.True(second!.FromCache);
        Assert.Equal(1, calls);
        Assert.Equal(first.Text, svc.GetCached(Sample)!.Text);
        Assert.Equal(first.Text, svc.GetLatestForPath(@"d:\stuff\THING.dat")!.Text);
    }

    [Fact]
    public async Task Unusable_answers_are_not_cached()
    {
        using var dir = new TempTree();
        int calls = 0;
        using var svc = new ExplanationService(new AiPaths(dir.Root), (_, _, _) => { calls++; return Task.FromResult("You should keep it."); });
        svc.Settings.Enabled = true;
        Assert.Null(await svc.ExplainAsync(Sample, null, default));
        Assert.Null(await svc.ExplainAsync(Sample, null, default));
        Assert.Equal(2, calls);
        Assert.Equal(0, svc.CachedCount);
    }

    [Fact]
    public void Not_set_up_until_turned_on_with_a_model()
    {
        using var dir = new TempTree();
        using var svc = new ExplanationService(new AiPaths(dir.Root));
        Assert.Equal(AiStatus.NotSetUp, svc.Status);
        svc.Settings.Enabled = true;
        Assert.Equal(AiStatus.NotSetUp, svc.Status); // no model downloaded
        svc.Settings.CustomModelPath = dir.File("my.gguf", 10);
        Assert.Equal(AiStatus.Ready, svc.Status);
        Assert.Null(svc.GetCached(Sample));
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var dir = new TempTree();
        var paths = new AiPaths(dir.Root);
        new AiSettings { Enabled = true, AutoExplain = false, ModelId = "x" }.Save(paths);
        var loaded = AiSettings.Load(paths);
        Assert.True(loaded.Enabled);
        Assert.False(loaded.AutoExplain);
        Assert.Equal("x", loaded.ModelId);
        File.WriteAllText(paths.Settings, "{ not json");
        Assert.False(AiSettings.Load(paths).Enabled);
    }
}

public sealed class ModelDownloaderTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    private static ModelInfo Model(byte[]? content = null) => new("test", "Test model", "test.gguf", "https://example.invalid/test.gguf",
        Content.Length, Convert.ToHexStringLower(SHA256.HashData(content ?? Content)), "MIT", "tests", "");

    /// <summary>Serves <see cref="Content"/>, honouring Range requests, and records what was asked.</summary>
    private sealed class FakeServer(byte[] body, bool supportRange = true) : HttpMessageHandler
    {
        public List<long?> RangeStarts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long? from = request.Headers.Range?.Ranges.First().From;
            RangeStarts.Add(from);
            if (from is long f && supportRange)
            {
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[(int)f..]) };
                return Task.FromResult(partial);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public async Task Downloads_checks_and_marks_ready()
    {
        using var dir = new TempTree();
        var m = Model();
        Assert.Equal(ModelState.NotDownloaded, ModelDownloader.GetState(m, dir.Root));
        string path = await ModelDownloader.DownloadAsync(m, dir.Root, null, default, new FakeServer(Content));
        Assert.Equal(Content, File.ReadAllBytes(path));
        Assert.Equal(ModelState.Ready, ModelDownloader.GetState(m, dir.Root));

        ModelDownloader.Delete(m, dir.Root);
        Assert.Equal(ModelState.NotDownloaded, ModelDownloader.GetState(m, dir.Root));
        Assert.Empty(Directory.GetFiles(dir.Root));
    }

    [Fact]
    public async Task Interrupted_download_resumes_where_it_stopped()
    {
        using var dir = new TempTree();
        var m = Model();
        File.WriteAllBytes(Path.Combine(dir.Root, "test.gguf.part"), Content[..120_000]);
        Assert.Equal(ModelState.Partial, ModelDownloader.GetState(m, dir.Root));

        var server = new FakeServer(Content);
        string path = await ModelDownloader.DownloadAsync(m, dir.Root, null, default, server);
        Assert.Equal([120_000L], server.RangeStarts);
        Assert.Equal(Content, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Server_without_resume_support_starts_over()
    {
        using var dir = new TempTree();
        var m = Model();
        File.WriteAllBytes(Path.Combine(dir.Root, "test.gguf.part"), new byte[50_000]); // garbage start
        string path = await ModelDownloader.DownloadAsync(m, dir.Root, null, default, new FakeServer(Content, supportRange: false));
        Assert.Equal(Content, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Damaged_download_is_refused_and_removed()
    {
        using var dir = new TempTree();
        byte[] tampered = (byte[])Content.Clone();
        tampered[1234] ^= 0xFF;
        var m = Model();
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelDownloader.DownloadAsync(m, dir.Root, null, default, new FakeServer(tampered)));
        Assert.Equal(ModelState.NotDownloaded, ModelDownloader.GetState(m, dir.Root));
        Assert.Empty(Directory.GetFiles(dir.Root));
    }

    [Fact]
    public async Task A_complete_file_copied_in_is_checked_without_downloading()
    {
        using var dir = new TempTree();
        var m = Model();
        File.WriteAllBytes(Path.Combine(dir.Root, "test.gguf"), Content);
        Assert.Equal(ModelState.NeedsCheck, ModelDownloader.GetState(m, dir.Root));
        var server = new FakeServer(Content);
        await ModelDownloader.DownloadAsync(m, dir.Root, null, default, server);
        Assert.Empty(server.RangeStarts);
        Assert.Equal(ModelState.Ready, ModelDownloader.GetState(m, dir.Root));
    }

    [Fact]
    public void Catalog_models_are_pinned_and_free()
    {
        Assert.NotEmpty(ModelCatalog.All);
        Assert.Single(ModelCatalog.All, m => m.Recommended);
        foreach (var m in ModelCatalog.All)
        {
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.True(m.SizeBytes > 100_000_000);
            Assert.StartsWith("https://huggingface.co/", m.Url);
            Assert.EndsWith(m.FileName, m.Url);
            Assert.Contains(m.License, new[] { "Apache-2.0", "MIT" });
        }
    }
}

public sealed class AiTargetTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();

    private static FileSystemEntry Entry(bool dir, AnalysisInfo? a, int depth = 2) => new()
    {
        Id = 5, Name = dir ? "Folder" : "x.zzq", Path = @"D:\a\x", IsDirectory = dir, Depth = depth, Analysis = a,
    };

    [Fact]
    public void Only_items_nothing_identifies_are_targets()
    {
        Assert.True(AiTargets.NeedsExplanation(Entry(false, new AnalysisInfo { Category = "other" })));
        Assert.True(AiTargets.NeedsExplanation(Entry(true, new AnalysisInfo())));
        Assert.False(AiTargets.NeedsExplanation(Entry(false, new AnalysisInfo { Category = "image" })));
        Assert.False(AiTargets.NeedsExplanation(Entry(false, new AnalysisInfo { Category = "other", OwnerAppId = 3 })));
        Assert.False(AiTargets.NeedsExplanation(Entry(true, new AnalysisInfo { LocationRuleId = "temp" })));
        Assert.False(AiTargets.NeedsExplanation(Entry(true, new AnalysisInfo(), depth: 0)));
        Assert.False(AiTargets.NeedsExplanation(Entry(false, null)));
    }

    [Fact]
    public void Database_lists_the_biggest_top_level_unidentified_items()
    {
        using var tree = new TempTree();
        using var output = new TempTree();
        tree.File(@"Mystery\a.zzq", 3_000_000);
        tree.File(@"Mystery\Inner\b.zzq", 2_000_000);  // inside an unidentified folder: covered by it
        tree.File("loose.zzq", 1_500_000);              // unknown type at the top
        tree.File("photo.jpg", 4_000_000);              // known type: not a target
        tree.File("tiny.zzq", 1000);                    // too small
        string dbPath = Path.Combine(output.Root, "scan.db");
        new ScanPipeline(Kb, PathTokens.ForThisMachine(), new AnalysisOptions { IncludeMsiComponents = false, Gentle = false })
            .Run(new ScanOptions { RootPath = tree.Root }, dbPath, null, default);

        // The test folder lives under %TEMP%, a known location; pretend it isn't so the rule itself is tested.
        using (var c = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE analysis SET location_rule = NULL, owner_app_id = NULL";
            cmd.ExecuteNonQuery();
        }

        using var db = ScanDatabase.Open(dbPath);
        var items = db.GetUnidentifiedItems(10, 1L << 20);
        Assert.Equal(["Mystery", "loose.zzq"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.True(AiTargets.NeedsExplanation(i)));

        var report = ReportData.From(db, Kb, aiNote: p => p.EndsWith("loose.zzq") ? "A guess." : null);
        Assert.Equal(["Mystery", "loose.zzq"], report.Unidentified.Select(i => i.Name));
        Assert.Equal("A guess.", report.Unidentified[1].AiNote);
        Assert.Contains(report.Coverage, n => n.Topic == "Local AI explanations" && n.Status == "On");
        Assert.Contains(ReportData.From(db, Kb).Coverage, n => n.Topic == "Local AI explanations" && n.Status == "Off");
    }
}

/// <summary>Runs the real downloaded model, when it's on this PC (skipped quietly otherwise).</summary>
public sealed class LocalModelTests
{
    [Fact]
    [Trait("Category", "LocalModel")]
    public async Task Real_model_explains_without_advice()
    {
        var paths = new AiPaths();
        var m = ModelCatalog.Default;
        if (ModelDownloader.GetState(m, paths.Models) != ModelState.Ready) return; // model not downloaded on this PC

        using var dir = new TempTree();
        using var svc = new ExplanationService(new AiPaths(dir.Root));
        svc.Settings.Enabled = true;
        svc.Settings.CustomModelPath = ModelDownloader.FinalPath(m, paths.Models);

        var facts = ExplainFacts.Create("DXCache", true,
        [
            ("Location", @"C:\Users\<you>\AppData\Local\NVIDIA"),
            ("Contains", "1,204 files in 0 folders, 1.2 GB in total"),
            ("Biggest items inside", "6f2c1a.toc (40 MB), 6f2c1a.bin (38 MB)"),
        ]);
        var e = await svc.ExplainAsync(facts, null, default);
        Assert.NotNull(e);
        Assert.Contains("NVIDIA", e.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<think>", e.Text);
        Assert.False(ExplanationCleaner.IsAdvice(e.Text));
    }
}
