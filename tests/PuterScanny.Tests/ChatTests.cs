using System.Security.Cryptography;
using PuterScanny.Analysis;
using PuterScanny.Chat;
using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

/// <summary>A made-up Downloads folder, scanned once and shared by the chat tests.</summary>
public sealed class ChatScanFixture : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _out = new();

    public ChatScanFixture()
    {
        string dl = @"Users\Sam\Downloads\";
        var rng = new Random(7);
        void File(string rel, int size, int ageDays)
        {
            string path = _tree.File(dl + rel, 0);
            var bytes = new byte[size];
            rng.NextBytes(bytes);
            System.IO.File.WriteAllBytes(path, bytes);
            System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
            System.IO.File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        }

        File(@"retip-1.15-win-amd64\retip.exe", 4000, 400);
        File(@"retip-1.15-win-amd64\rexruntime.dll", 1000, 400);
        File(@"retip-1.15-win-amd64\Viva Pinata (Disc 1).iso", 60_000, 400);
        File("OllamaSetup.exe", 7000, 200);
        File("blender-4.0.2-windows-x64.msi", 6000, 300);
        File(@"Videos\holiday.mp4", 30_000, 800);
        File(@"Videos\party.mp4", 12_000, 10);
        File(@"Games2\save.dat", 5000, 900);
        File(@"Games\notes.txt", 700, 900);
        File("DogCompanion.rar-725-1-1-0-1661494986.rar", 2000, 500);
        File("mystery.qqz", 1234, 400);
        File(@"Photos\beach.jpg", 9000, 600);
        // Two identical copies.
        File(@"Photos\cat.jpg", 70_000, 700); // duplicates under 64 KB aren't counted
        Directory.CreateDirectory(Path.Combine(_tree.Root, dl, "Backup"));
        System.IO.File.Copy(Path.Combine(_tree.Root, dl, @"Photos\cat.jpg"), Path.Combine(_tree.Root, dl, @"Backup\cat (copy).jpg"));
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(_tree.Root, dl, @"Backup\cat (copy).jpg"), DateTime.UtcNow.AddDays(-700));
        // A game in a folder of its own, and an unpacked mod download with a readme.
        File(@"Gba\Legend of Zelda, The - The Minish Cap (USA)\Legend of Zelda, The - The Minish Cap (USA).gba", 3000, 500);
        // ROM sets packed with TorrentZIP all carry the same date, 24 Dec 1996 23:32 (local time once unpacked).
        System.IO.File.SetLastWriteTime(Path.Combine(_tree.Root, dl, @"Gba\Legend of Zelda, The - The Minish Cap (USA)\Legend of Zelda, The - The Minish Cap (USA).gba"),
            new DateTime(1996, 12, 24, 23, 32, 0, DateTimeKind.Local));
        File(Mod + @"\BmGame\Movies\101_Batmobile_Intro.bik", 5000, 500);
        File(Mod + @"\BmGame\Movies\213_Bane_intro.bik", 4000, 500);
        System.IO.File.WriteAllText(Path.Combine(_tree.Root, dl, Mod, "readme.txt"),
            "Thanks for using Asylum 4K ->REMASTERED<- !\r\n\r\nMod created by\r\nhttps://example.com/someone\r\n\r\nInstallation:\r\n" +
            "Just drag the BmGame folder into the main folder of the game.\r\nOverwrite existing files.\r\n");
        // Leftovers from a Mac, and a code project (whose node_modules packages are not projects of their own).
        File(@"Wallpapers\__MACOSX\._beach.jpg", 120, 500);
        File(@"Projects\bot\package.json", 300, 50);
        File(@"Projects\bot\index.js", 900, 50);
        File(@"Projects\bot\.git\HEAD", 30, 50);
        File(@"Projects\bot\node_modules\left-pad\package.json", 200, 50);

        DbPath = Path.Combine(_out.Root, "scan.db");
        var tokens = new PathTokens(new Dictionary<string, string> { ["users"] = Path.Combine(_tree.Root, "Users") });
        new ScanPipeline(Kb, tokens, new AnalysisOptions
            {
                IncludeMsiComponents = false, IdentifyContents = true /* needed for duplicates */, VerifySignatures = false, Gentle = false,
                PortableAppAreaOverride = _ => false,
            })
            .Run(new ScanOptions { RootPath = Path.Combine(_tree.Root, dl), IncludeAlternateStreams = false }, DbPath, null, default);
        Downloads = Path.Combine(_tree.Root, dl).TrimEnd('\\');
        Db = ScanDatabase.Open(DbPath);
    }

    public const string Mod = "Asylum 4K - REMASTERED (Part 1)-36-2-0-1669218064";

    public static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();
    public string DbPath { get; }
    public string Downloads { get; }
    public ScanDatabase Db { get; }

    public IReadOnlyList<FileSystemEntry> All()
    {
        using var read = Db.OpenReadConnection();
        return ScanDatabase.ReadAllEntries(read).ToList();
    }

    public ChatEngine Engine(IChatModel? model = null) => new(new ScanLookup(Db, Kb), model);

    public void Dispose()
    {
        Db.Dispose();
        _tree.Dispose();
        _out.Dispose();
    }
}

public sealed class ChatTests(ChatScanFixture scan) : IClassFixture<ChatScanFixture>
{
    private string Dl => scan.Downloads;

    private static Task<ChatAnswer> Ask(ChatEngine engine, string q) => engine.AskAsync(q, null, CancellationToken.None);

    // ---------------------------------------------------------------- recognised questions

    [Theory]
    [InlineData("What can I safely delete?", LookupKind.List, ListFilter.Delete)]
    [InlineData("how much space can I free up", LookupKind.List, ListFilter.Delete)]
    [InlineData("What's taking the most space?", LookupKind.List, ListFilter.Any)]
    [InlineData("What should I review myself?", LookupKind.List, ListFilter.Review)]
    [InlineData("Are there any duplicates?", LookupKind.Duplicates, ListFilter.Any)]
    [InlineData("Which programs don't I use?", LookupKind.Programs, ListFilter.Any)]
    [InlineData("which programs should I uninstall", LookupKind.Programs, ListFilter.Delete)]
    [InlineData("what kinds of files are here", LookupKind.Types, ListFilter.Any)]
    [InlineData("summary", LookupKind.Overview, ListFilter.Any)]
    [InlineData("anything nobody can identify?", LookupKind.List, ListFilter.Unidentified)]
    [InlineData("Did I leave any game mods lying around?", LookupKind.List, ListFilter.Mods)]
    [InlineData("what must I not delete", LookupKind.List, ListFilter.Keep)]
    [InlineData("show me empty folders", LookupKind.List, ListFilter.EmptyFolders)]
    public void Common_questions_are_recognised(string question, LookupKind kind, ListFilter filter)
    {
        var request = new QuestionMatcher(new ScanLookup(scan.Db, ChatScanFixture.Kb)).Match(question, Dl);
        Assert.NotNull(request);
        Assert.Equal(kind, request.Kind);
        Assert.Equal(filter, request.Filter);
    }

    [Fact]
    public void Folders_kinds_sizes_and_times_in_a_question_become_filters()
    {
        var matcher = new QuestionMatcher(new ScanLookup(scan.Db, ChatScanFixture.Kb));

        var r = matcher.Match("Which videos in Videos haven't I watched in 2 years?", Dl)!;
        Assert.Equal(LookupKind.List, r.Kind);
        Assert.Equal("video", r.Category);
        Assert.Equal(730, r.UnusedDays);
        Assert.Equal(Path.Combine(Dl, "Videos"), r.Folder, ignoreCase: true);

        r = matcher.Match("show me the top 3 installers over 5 KB", Dl)!;
        Assert.Equal("installer", r.Category);
        Assert.Equal(5 * 1024, r.MinBytes);
        Assert.Equal(3, r.Count);

        // "Games" is a folder; "Games2" must not be mistaken for it.
        // "What's in Games?" describes that folder and what it holds.
        r = matcher.Match("what's in Games", Dl)!;
        Assert.Equal(LookupKind.Item, r.Kind);
        Assert.True(r.Explain);
        Assert.Equal(Path.Combine(Dl, "Games"), r.Name, ignoreCase: true);
        r = matcher.Match("how much space is in Games", Dl)!;
        Assert.Equal(LookupKind.Overview, r.Kind);
        Assert.Equal(Path.Combine(Dl, "Games"), r.Folder, ignoreCase: true);
    }

    [Fact]
    public void Questions_about_one_item_find_it()
    {
        var matcher = new QuestionMatcher(new ScanLookup(scan.Db, ChatScanFixture.Kb));
        foreach (string q in new[] { "what is OllamaSetup.exe", "Can I delete OllamaSetup.exe?", "why is OllamaSetup.exe marked for deletion?", "tell me about OllamaSetup.exe" })
        {
            var r = matcher.Match(q, Dl);
            Assert.True(r is { Kind: LookupKind.Item, Name: "OllamaSetup.exe" }, q);
        }
        // A kind of file, not a name: list them with what PuterScanny says about each.
        var iso = matcher.Match("can I delete the iso", Dl)!;
        Assert.Equal(LookupKind.List, iso.Kind);
        Assert.Equal("disk-image", iso.Category);
    }

    [Fact]
    public void Greetings_and_nonsense_do_not_pretend_to_answer()
    {
        var matcher = new QuestionMatcher(new ScanLookup(scan.Db, ChatScanFixture.Kb));
        Assert.Null(matcher.Match("hello", Dl));
        Assert.Null(matcher.Match("Is there anything here I should worry about?", Dl));
    }

    // ---------------------------------------------------------------- exact answers

    [Fact]
    public async Task Deletable_total_matches_the_scan_exactly()
    {
        var engine = scan.Engine();
        var answer = await Ask(engine, "What can I delete?");
        var deletable = scan.All().Where(e => !e.IsDirectory && e.Analysis?.Verdict is Verdict.RecommendDelete or Verdict.SafeToDelete).ToList();
        Assert.NotEmpty(deletable);
        Assert.Equal(AnswerSource.Scan, answer.Source);
        Assert.Contains(ByteSize.Format(deletable.Sum(e => e.Size)), answer.Text);
        Assert.Contains($"{deletable.Count:N0} file", answer.Text);
        Assert.All(answer.Items, i => Assert.True(i.IsDeletable, i.Name));
        // Never the portable program's disc image.
        Assert.DoesNotContain(answer.Items, i => i.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Asking_about_a_file_reports_PuterScannys_own_recommendation()
    {
        var engine = scan.Engine();
        var answer = await Ask(engine, "can I delete Viva Pinata (Disc 1).iso?");
        var iso = scan.All().Single(e => e.Name == "Viva Pinata (Disc 1).iso");
        Assert.Equal(AnswerSource.Scan, answer.Source);
        Assert.StartsWith("Viva Pinata (Disc 1).iso: Keep", answer.Text); // the item first, so it's clear what the verdict is about
        Assert.Contains("It's a disc image", answer.Text);
        Assert.Contains("retip", answer.Text);
        Assert.Equal(iso.Id, answer.Items[0].EntryId);
        Assert.False(answer.Items[0].IsDeletable);
    }

    [Fact]
    public async Task Questions_about_a_folder_only_count_what_is_inside_it()
    {
        var engine = scan.Engine();
        engine.ScopePath = Path.Combine(Dl, "Games");
        var answer = await Ask(engine, "What's taking the most space?");
        Assert.All(answer.Items, i => Assert.StartsWith(Path.Combine(Dl, "Games") + "\\", i.Path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(answer.Items, i => i.Name == "save.dat"); // in Games2, next door

        var overview = await Ask(engine, "summary");
        var games = scan.All().Single(e => e.Path.Equals(Path.Combine(Dl, "Games"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"holds {ByteSize.Format(games.TotalSize)} in 1 file", overview.Text);
    }

    [Fact]
    public async Task Unused_files_are_the_ones_neither_opened_nor_changed_in_that_time()
    {
        var engine = scan.Engine();
        var answer = await Ask(engine, "Which videos haven't I watched in a year?");
        Assert.Equal(["holiday.mp4"], answer.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Follow_up_questions_narrow_the_previous_answer()
    {
        var engine = scan.Engine();
        await Ask(engine, "what's in Videos");
        var answer = await Ask(engine, "and which of those are older than 2 years?");
        Assert.Equal(["holiday.mp4"], answer.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Duplicates_offer_only_the_extra_copy()
    {
        var engine = scan.Engine();
        var answer = await Ask(engine, "any duplicates?");
        var copy = Assert.Single(answer.Items);
        Assert.Contains("cat", copy.Name);
        Assert.Contains("1 set", answer.Text);
        Assert.Contains(ByteSize.Format(70_000), answer.Text);
    }

    [Fact]
    public async Task Every_exact_answer_passes_the_fact_check()
    {
        var engine = scan.Engine();
        var lookup = new ScanLookup(scan.Db, ChatScanFixture.Kb);
        var matcher = new QuestionMatcher(lookup);
        foreach (string q in new[]
                 {
                     "What can I delete?", "What's taking the most space?", "summary", "what kinds of files are here", "any duplicates?",
                     "what is OllamaSetup.exe", "Which programs don't I use?", "what should I review", "oldest files", "biggest folders",
                     "what is BmGame", "what is the minish cap", "what's in Videos", "what is " + ChatScanFixture.Mod,
                 })
        {
            var request = matcher.Match(q, Dl)!;
            var result = lookup.Run(request, Dl);
            var outcome = AnswerChecker.Check(result.Answer, q, [result]);
            Assert.True(outcome.Ok, $"{q}: {string.Join("; ", outcome.Problems)}");
        }
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- describing one folder or file

    [Theory]
    [InlineData("What is this folder?", true)]
    [InlineData("what is this", true)]
    [InlineData("tell me about this folder", true)]
    [InlineData("What are these files in this folder?", true)]
    [InlineData("what files are in here", true)]
    [InlineData("list the files", true)]
    [InlineData("what's in here", true)]
    [InlineData("what are these", true)]
    [InlineData("can I delete this folder?", false)]
    [InlineData("is it safe to delete?", false)]
    public void Questions_about_this_folder_mean_the_folder_being_asked_about(string question, bool explain)
    {
        var matcher = new QuestionMatcher(new ScanLookup(scan.Db, ChatScanFixture.Kb));
        var r = matcher.Match(question, Path.Combine(Dl, ChatScanFixture.Mod))!;
        Assert.Equal(LookupKind.Item, r.Kind);
        Assert.Null(r.Name); // = the folder the chat is about, never a file whose name happens to contain "about" or "folder"
        Assert.Equal(explain, r.Explain);
    }

    [Fact]
    public async Task Describing_a_folder_says_what_is_inside_what_it_is_part_of_and_what_its_readme_says()
    {
        var engine = scan.Engine();
        var answer = await Ask(engine, "What is BmGame?");
        Assert.Equal("BmGame", answer.Items[0].Name);
        Assert.Contains($"part of {ChatScanFixture.Mod}, a mod download", answer.Text);
        Assert.Contains("Review", answer.Text); // the mod download's recommendation applies to what's in it
        Assert.Contains("Bink", answer.Text);
        Assert.Contains("213_Bane_intro.bik", answer.Text);
        Assert.Contains("in the folder Movies", answer.Text);
        Assert.Contains("Just drag the BmGame folder into the main folder of the game.", answer.Text);
        Assert.DoesNotContain("https://", answer.Text);
        Assert.Contains(answer.Items, i => i.Name == ChatScanFixture.Mod);
    }

    [Fact]
    public async Task Part_of_a_name_finds_the_item_and_a_one_game_folder_means_the_game()
    {
        var engine = scan.Engine();
        var answer = await Ask(engine, "what is the minish cap");
        Assert.StartsWith("Legend of Zelda, The - The Minish Cap (USA).gba is a Game Boy Advance ROM", answer.Text);
        Assert.Contains("It's the only file in the folder Legend of Zelda, The - The Minish Cap (USA).", answer.Text);
        // Its 1996 date is TorrentZIP's, not a real change: say when it arrived instead.
        Assert.Contains("TorrentZIP", answer.Text);
        Assert.Contains("added to this PC ", answer.Text);
        Assert.DoesNotContain("29.", answer.Text);
    }

    [Fact]
    public async Task Answers_about_one_item_are_plain_and_free_of_boilerplate()
    {
        var engine = scan.Engine();
        foreach (string q in new[] { "What is BmGame?", "what is the minish cap", "what is OllamaSetup.exe", "what is retip.exe", "what is mystery.qqz" })
        {
            var answer = await Ask(engine, q);
            Assert.DoesNotContain("PuterScanny", answer.Text);
            Assert.DoesNotContain("never suggests", answer.Text);
            Assert.Contains("Recommendation: ", answer.Text);
        }
    }

    [Theory]
    [InlineData("Which of these are Game Boy games?", LookupKind.List, null)]
    [InlineData("How much space do my game files take up?", LookupKind.List, null)]
    [InlineData("Is it safe to remove the Skyrim mod files?", LookupKind.List, null)]
    [InlineData("What did I download most recently?", LookupKind.List, null)]
    [InlineData("Are there any files from a Mac?", LookupKind.Topic, "mac")]
    [InlineData("Do I have any machine learning models in here?", LookupKind.Topic, "ai-models")]
    [InlineData("Which folders look like coding projects?", LookupKind.Topic, "code-projects")]
    [InlineData("any screenshots?", LookupKind.Topic, "screenshots")]
    public void Questions_about_games_topics_and_recent_downloads_are_read_correctly(string question, LookupKind kind, string? topic)
    {
        var r = new QuestionMatcher(new ScanLookup(scan.Db, ChatScanFixture.Kb)).Match(question, Dl)!;
        Assert.Equal(kind, r.Kind);
        Assert.Equal(topic, r.Topic);
        switch (question)
        {
            case "Which of these are Game Boy games?":
                Assert.Equal("Game Boy games", r.TypeName);
                Assert.Contains("Game Boy Advance ROM", r.TypeLabels!);
                break;
            case "How much space do my game files take up?":
                Assert.Equal("game", r.Category);
                break;
            case "Is it safe to remove the Skyrim mod files?":
                Assert.Equal(ListFilter.Mods, r.Filter);
                Assert.Equal(EntryKind.Any, r.ItemKind); // the unpacked mod folders count too
                break;
            case "What did I download most recently?":
                Assert.Equal(EntrySort.Newest, r.Sort);
                break;
        }
    }

    [Fact]
    public async Task Topic_lookups_find_Mac_leftovers_and_code_projects_but_not_packages()
    {
        var engine = scan.Engine();
        var mac = await Ask(engine, "Are there any files from a Mac?");
        Assert.Equal(["__MACOSX"], mac.Items.Select(i => i.Name));

        var projects = await Ask(engine, "Which folders look like coding projects?");
        Assert.Equal(["bot"], projects.Items.Select(i => i.Name)); // not left-pad, a package inside node_modules

        var games = await Ask(engine, "Which of these are Game Boy games?");
        Assert.Equal(["Legend of Zelda, The - The Minish Cap (USA).gba"], games.Items.Select(i => i.Name));

        var none = await Ask(engine, "any screenshots?");
        Assert.Empty(none.Items);
        Assert.StartsWith("I looked for screenshots in Downloads and found none.", none.Text);
    }

    [Fact]
    public async Task Files_inside_a_mod_download_are_part_of_the_mod_not_your_own()
    {
        var video = scan.All().Single(e => e.Name == "213_Bane_intro.bik");
        Assert.Equal(Verdict.Review, video.Analysis!.Verdict);
        Assert.Equal("Part of a mod download (Asylum 4K - REMASTERED (Part 1))", video.Analysis.Headline);
        Assert.Equal(CleanupAction.None, video.Analysis.Action); // decided for the mod download as a whole
        var folder = scan.All().Single(e => e.Name == "BmGame");
        Assert.NotEqual(Verdict.KeepPersonal, folder.Analysis!.Verdict);

        var answer = await Ask(scan.Engine(), "can I delete BmGame?");
        Assert.StartsWith("BmGame: Review, for all of " + ChatScanFixture.Mod, answer.Text);
    }

    [Fact]
    public async Task Can_I_delete_it_after_asking_what_something_is_puts_the_recommendation_first()
    {
        var engine = scan.Engine();
        await Ask(engine, "what is OllamaSetup.exe");
        var answer = await Ask(engine, "can I delete it?");
        Assert.StartsWith("OllamaSetup.exe: ", answer.Text);
    }

    [Fact]
    public async Task A_follow_up_after_describing_a_folder_narrows_to_that_folder()
    {
        var engine = scan.Engine();
        await Ask(engine, "what's in Videos");
        var answer = await Ask(engine, "which of those are older than 2 years?");
        Assert.Equal(["holiday.mp4"], answer.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Asking_questions_never_changes_the_scan()
    {
        static byte[] Hash(string path)
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(s);
        }
        byte[] before = Hash(scan.DbPath);
        var engine = scan.Engine();
        foreach (string q in new[] { "What can I delete?", "summary", "any duplicates?", "what is mystery.qqz", "which programs should I uninstall" })
            await Ask(engine, q);
        Assert.Equal(before, Hash(scan.DbPath));
    }

    // ---------------------------------------------------------------- the local AI route

    private sealed class FakeModel(params string[] replies) : IChatModel
    {
        private int _next;
        public List<(string System, string User, string? Grammar)> Calls { get; } = [];
        public string Name => "Test model";
        public bool IsAvailable => true;
        public PuterScanny.AI.AiCapacity Capacity { get; init; } = PuterScanny.AI.AiCapacity.Unlimited;

        public Task<string> GenerateAsync(string system, string user, int maxTokens, string? grammar, float temperature, CancellationToken ct)
        {
            Calls.Add((system, user, grammar));
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }
    }

    private const string PlanDelete =
        """{"lookups":[{"lookup":"list","folder":null,"name":null,"kind":"any","filter":"delete","category":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";

    /// <summary>The answer as the app ends up showing it: the summary (when the AI adds one and it passes) above the facts.</summary>
    private static async Task<string> Shown(ChatAnswer a) =>
        a.Summary is not null && await a.Summary(null, CancellationToken.None) is { } summary ? summary + "\n\n" + a.Text : a.Text;

    private const string PlanMods =
        """{"lookups":[{"lookup":"list","folder":null,"name":null,"kind":"any","filter":"mods","category":null,"topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";

    private static PuterScanny.AI.AiCapacity Tier(PuterScanny.AI.AiTier tier, bool summaries = false) =>
        new(tier, summaries, tier == PuterScanny.AI.AiTier.CantRun ? "only 1 GB of memory is free right now, and the model needs about 3.6 GB" : "test",
            0, 0, 0, true, 8, false, null);

    [Fact]
    public async Task A_question_read_with_confidence_never_calls_the_AI()
    {
        var model = new FakeModel(PlanDelete);
        var answer = await Ask(scan.Engine(model), "What can I safely delete?");
        Assert.Equal(AnswerSource.Scan, answer.Source);
        Assert.Empty(model.Calls);
        Assert.Null(answer.Summary);
    }

    [Fact]
    public async Task An_unsure_reading_goes_to_the_AI_with_the_apps_own_reading_as_a_hint()
    {
        var model = new FakeModel(PlanMods);
        var answer = await Ask(scan.Engine(model), "Is it safe to remove the Skyrim mod files?");
        Assert.Equal(AnswerSource.AiUnderstood, answer.Source);
        Assert.Contains("Simple rules read this question as \"biggest mod downloads", model.Calls[0].User);
        Assert.Contains("mod downloads", answer.Text); // the answer itself is the scan's
    }

    [Fact]
    public async Task Without_the_AI_an_unsure_reading_says_how_the_question_was_read()
    {
        var answer = await Ask(scan.Engine(), "Is it safe to remove the Skyrim mod files?");
        Assert.Equal(AnswerSource.ScanUnsure, answer.Source);
        Assert.Contains("I read this as: biggest mod downloads in Downloads. \"skyrim\" didn't fit that reading", answer.Text);
        Assert.Contains("set up", answer.Note);
    }

    [Fact]
    public async Task When_this_PC_cant_run_the_AI_it_isnt_used_and_the_answer_says_why()
    {
        var model = new FakeModel(PlanMods) { Capacity = Tier(PuterScanny.AI.AiTier.CantRun) };
        var unsure = await Ask(scan.Engine(model), "Is it safe to remove the Skyrim mod files?");
        Assert.Equal(AnswerSource.ScanUnsure, unsure.Source);
        Assert.Contains("wasn't used: only 1 GB of memory is free", unsure.Note);
        var help = await Ask(scan.Engine(model), "got any neural nets lying around?");
        Assert.Equal(AnswerSource.Help, help.Source);
        Assert.Contains("wasn't used: only 1 GB of memory is free", help.Text);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task A_summary_sentence_about_the_results_instead_of_the_files_is_dropped()
    {
        var model = new FakeModel("BmGame holds the mod's Bink cutscene videos. The facts provided only show that it is a folder.");
        var answer = await Ask(scan.Engine(model), "What is BmGame?");
        string shown = await Shown(answer);
        Assert.StartsWith("BmGame holds the mod's Bink cutscene videos.\n\n", shown);
        Assert.DoesNotContain("facts provided", shown);
    }

    [Fact]
    public async Task Something_asked_about_by_name_is_searched_for_by_the_names_its_files_are_likely_to_have()
    {
        // "Batman": nothing in the scan is named that, but the AI knows the names its files go by.
        const string plan = """{"lookups":[{"lookup":"list","folder":null,"name":null,"terms":["batman","arkham","asylum","game"],"kind":"any","filter":"any","category":null,"topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";
        var model = new FakeModel(plan, "These are probably Batman mod files, going by their names.");
        var answer = await Ask(scan.Engine(model), "Is it safe to remove the Batman mod files?");
        Assert.Equal(AnswerSource.AiUnderstood, answer.Source);
        Assert.Equal([ChatScanFixture.Mod], answer.Items.Select(i => i.Name)); // the folder once, not the files inside it
        Assert.Contains("name has \"asylum\"", answer.Items[0].Note); // what it was found by is shown
        Assert.Contains("with \"batman\", \"arkham\" or \"asylum\" in the name", answer.Text); // "game" is too general to search for
        Assert.Contains("Review", answer.Text);
    }

    [Fact]
    public void Search_words_from_the_AI_are_cleaned_up()
    {
        var r = AiPlanner.Parse("""{"lookups":[{"lookup":"list","folder":null,"name":null,"terms":["Zelda","zelda","GAME","ab","link","hyrule","nintendo","majora","ocarina"],"kind":"any","filter":"any","category":null,"topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""");
        Assert.Equal(["zelda", "link", "hyrule", "nintendo", "majora", "ocarina"], r[0].Terms!);
    }

    [Fact]
    public async Task When_the_AI_names_something_that_isnt_a_file_or_folder_its_names_are_searched()
    {
        const string plan = """{"lookups":[{"lookup":"item","folder":null,"name":"Batman","terms":["arkham","asylum"],"kind":"any","filter":"any","category":null,"topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";
        var answer = await Ask(scan.Engine(new FakeModel(plan, "x")), "Is it safe to remove the Batman mod files?");
        Assert.Equal([ChatScanFixture.Mod], answer.Items.Select(i => i.Name));
        Assert.Contains("with \"arkham\" or \"asylum\" in the name", answer.Text);

        const string exact = """{"lookups":[{"lookup":"item","folder":null,"name":"BmGame","terms":null,"kind":"any","filter":"any","category":null,"topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";
        var described = await Ask(scan.Engine(new FakeModel(exact, "x")), "what's that bm folder?");
        Assert.Equal("BmGame", described.Items[0].Name); // an exact name stays a lookup of that item
    }

    [Fact]
    public async Task Without_the_AI_words_the_reading_cant_place_are_looked_for_in_names()
    {
        var answer = await Ask(scan.Engine(), "where are my Zelda games?");
        Assert.Equal(AnswerSource.Scan, answer.Source);
        Assert.StartsWith("Legend of Zelda, The - The Minish Cap (USA)", answer.Items[0].Name);
        Assert.Contains("\"zelda\" in the name", answer.Text);
        Assert.DoesNotContain("I read this as", answer.Text);
    }

    [Fact]
    public async Task A_whole_title_is_also_searched_by_its_words_and_without_filters_when_nothing_matches()
    {
        // Full titles with punctuation, inside a filter that finds nothing: the title's own words still find the mod folder.
        const string plan = """{"lookups":[{"lookup":"list","folder":null,"name":null,"terms":["batman: arkham asylum","asylum"],"kind":"file","filter":"mods","category":null,"topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";
        Assert.Equal(["batman: arkham asylum", "asylum"], AiPlanner.Parse(plan)[0].Terms!);
        var answer = await Ask(scan.Engine(new FakeModel(plan, "x")), "Is it safe to remove the Batman mod files?");
        Assert.Equal([ChatScanFixture.Mod], answer.Items.Select(i => i.Name));
        Assert.DoesNotContain("I found no", answer.Text); // the wider search's answer on its own

        // "legend of zelda" finds "Legend of Zelda, The - …" whatever separates the words.
        var q = new EntryQuery { Scope = Dl, Kind = EntryKind.File, Limit = 50, NameContainsAny = ["legend of zelda"] };
        Assert.Single(scan.Db.QueryEntries(q));
    }

    [Fact]
    public async Task A_summary_calling_something_safe_to_remove_needs_a_safe_label()
    {
        const string plan = """{"lookups":[{"lookup":"list","folder":null,"name":null,"terms":["asylum"],"kind":"any","filter":"any","category":null,"topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";
        var model = new FakeModel(plan, "It is safe to remove the Asylum mod because it is marked Review. It's an unpacked mod download.");
        string shown = await Shown(await Ask(scan.Engine(model), "Is it safe to remove the Asylum stuff?"));
        Assert.DoesNotContain("safe to remove the Asylum", shown);

        var recommended = new FakeModel(plan, "One Asylum folder is marked Review. It is recommended to delete this item based on that label.");
        shown = await Shown(await Ask(scan.Engine(recommended), "Is it safe to remove the Asylum stuff?"));
        Assert.DoesNotContain("recommended to delete", shown);
    }

    [Fact]
    public void Short_search_words_only_match_whole_words()
    {
        var q = new EntryQuery { Scope = Dl, Kind = EntryKind.File, Limit = 50 };
        Assert.DoesNotContain(scan.Db.QueryEntries(q with { NameContainsAny = ["art"] }), e => e.Name == "party.mp4"); // not inside "party"
        Assert.Contains(scan.Db.QueryEntries(q with { NameContainsAny = ["cat"] }), e => e.Name == "cat.jpg");
        Assert.Contains(scan.Db.QueryEntries(q with { NameContainsAny = ["party"] }), e => e.Name == "party.mp4");
    }

    [Fact]
    public async Task On_a_slow_PC_the_AI_only_works_out_questions_and_adds_no_summaries()
    {
        var model = new FakeModel(PlanDelete, "Should never be asked for.") { Capacity = Tier(PuterScanny.AI.AiTier.Slow) };
        var answer = await Ask(scan.Engine(model), "I'm running out of room, where do I start?");
        Assert.Equal(AnswerSource.AiUnderstood, answer.Source);
        Assert.Null(answer.Summary);
        Assert.Single(model.Calls);
        var described = await Ask(scan.Engine(model), "What is BmGame?");
        Assert.Null(described.Summary);
    }

    [Fact]
    public async Task A_description_shows_its_facts_first_and_the_AI_summary_goes_on_top_when_it_passes()
    {
        var model = new FakeModel("BmGame is part of an unpacked mod download. Its Bink videos are probably the game's cutscenes.");
        var answer = await Ask(scan.Engine(model), "What is BmGame?");
        Assert.Equal(AnswerSource.Scan, answer.Source);
        Assert.Contains("Recommendation: Review", answer.Text);   // shown at once, without waiting for the AI
        Assert.Empty(model.Calls);
        string shown = await Shown(answer);
        Assert.StartsWith("BmGame is part of an unpacked mod download.", shown);
        Assert.Contains("Just drag the BmGame folder", shown);
        Assert.Equal(AnswerChecker.DescribeSystem, model.Calls[0].System);

        // The mod download is marked Review: a summary saying it's recommended to keep is never shown.
        var wrong = new FakeModel("BmGame holds game videos. PuterScanny recommends keeping this folder.");
        answer = await Ask(scan.Engine(wrong), "What is BmGame?");
        Assert.Null(await answer.Summary!(null, CancellationToken.None));
        Assert.Equal(2, wrong.Calls.Count);
    }

    [Fact]
    public async Task The_AI_picks_lookups_the_answer_is_the_scans_and_a_checked_summary_can_go_on_top()
    {
        var deletable = scan.All().Where(e => !e.IsDirectory && e.Analysis?.Verdict is Verdict.RecommendDelete or Verdict.SafeToDelete).ToList();
        string total = ByteSize.Format(deletable.Sum(e => e.Size));
        string biggest = deletable.OrderByDescending(e => e.Size).First().Name;
        var model = new FakeModel(PlanDelete, $"{total} can be deleted here. The biggest is {biggest}.");
        var answer = await Ask(scan.Engine(model), "I'm running out of room, where do I start?");

        Assert.Equal(AnswerSource.AiUnderstood, answer.Source);
        Assert.Contains($"{total} in Downloads can be deleted", answer.Text);
        Assert.NotEmpty(answer.Items);
        Assert.NotNull(model.Calls[0].Grammar);     // planning is held to the lookup grammar
        string shown = await Shown(answer);
        Assert.StartsWith($"{total} can be deleted here.", shown);
        Assert.Contains(biggest, model.Calls[1].User);
        Assert.Null(model.Calls[1].Grammar);
    }

    [Fact]
    public async Task A_summary_with_made_up_numbers_is_never_shown()
    {
        var model = new FakeModel(PlanDelete, "You can free 42 GB by deleting setup.exe and 300 files.", "Still 42 GB, honestly.");
        var answer = await Ask(scan.Engine(model), "I'm running out of room, where do I start?");
        string shown = await Shown(answer);
        Assert.DoesNotContain("42 GB", shown);
        Assert.Contains("in Downloads can be deleted", shown);
    }

    [Fact]
    public async Task When_every_AI_lookup_comes_back_empty_it_says_what_was_searched()
    {
        const string plan = """{"lookups":[{"lookup":"list","folder":null,"name":null,"kind":"file","filter":"any","category":"database","topic":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";
        var model = new FakeModel(plan, "There are no machine learning models on your PC.");
        var answer = await Ask(scan.Engine(model), "got any neural nets lying around?");
        Assert.Equal("I found no databases in Downloads.", answer.Text);
        Assert.Null(answer.Summary);   // the AI never gets to write "there are none"
        Assert.Single(model.Calls);
    }

    [Theory]
    [InlineData(3L << 30, 1L << 30, null, false, PuterScanny.AI.AiTier.CantRun, false)]   // not enough free memory
    [InlineData(3L << 30, 8L << 30, 1.0, false, PuterScanny.AI.AiTier.CantRun, false)]    // too slow
    [InlineData(3L << 30, 8L << 30, 4.0, false, PuterScanny.AI.AiTier.Slow, false)]
    [InlineData(3L << 30, 8L << 30, 12.0, false, PuterScanny.AI.AiTier.Fast, true)]
    [InlineData(3L << 30, 8L << 30, 12.0, true, PuterScanny.AI.AiTier.Fast, false)]       // on battery: no summaries
    [InlineData(3L << 30, 8L << 30, null, false, PuterScanny.AI.AiTier.Fast, true)]       // not measured yet: a modern processor
    public void How_much_the_AI_is_used_depends_on_this_PC(long model, long free, double? speed, bool battery, PuterScanny.AI.AiTier tier, bool summaries)
    {
        var c = PuterScanny.AI.PcPower.Assess(model, modelLoaded: false, speed, threads: 8, total: 16L << 30, free, battery, avx2: true);
        Assert.Equal(tier, c.Tier);
        Assert.Equal(summaries, c.Summaries);
        Assert.False(string.IsNullOrWhiteSpace(c.Reason));
    }

    [Fact]
    public void A_loaded_model_doesnt_need_the_memory_again()
    {
        var c = PuterScanny.AI.PcPower.Assess(3L << 30, modelLoaded: true, 12.0, 8, 16L << 30, 1L << 30, false, true);
        Assert.Equal(PuterScanny.AI.AiTier.Fast, c.Tier);
    }

    [Fact]
    public async Task Without_the_AI_unrecognised_questions_get_help_not_guesses()
    {
        var answer = await Ask(scan.Engine(), "Is there anything here I should worry about?");
        Assert.Equal(AnswerSource.Help, answer.Source);
        Assert.Empty(answer.Items);
        Assert.Contains("Local AI", answer.Text);
    }

    [Fact]
    public void Planner_replies_are_read_strictly()
    {
        var r = Assert.Single(AiPlanner.Parse(PlanDelete));
        Assert.Equal(LookupKind.List, r.Kind);
        Assert.Equal(ListFilter.Delete, r.Filter);

        Assert.Empty(AiPlanner.Parse("I think you should delete everything"));
        Assert.Empty(AiPlanner.Parse("""{"lookups":[{"lookup":"format_drive"}]}"""));
        var odd = Assert.Single(AiPlanner.Parse("""{"lookups":[{"lookup":"list","category":"weapons","count":5000}]}"""));
        Assert.Null(odd.Category);
        Assert.Equal(ScanLookup.MaxCount, odd.Count);
    }

    [Fact]
    public void The_grammar_lists_every_lookup_and_filter_the_parser_reads()
    {
        string grammar = AiPlanner.Grammar(ChatScanFixture.Kb);
        foreach (string word in new[] { "overview", "list", "item", "duplicates", "programs", "types", "delete", "review", "no_reason", "unidentified", "mods", "least_used" })
            Assert.Contains($"\\\"{word}\\\"", grammar);
        foreach (string category in AiPlanner.Categories)
            Assert.NotNull(ChatScanFixture.Kb.GetCategory(category));
    }

    // ---------------------------------------------------------------- the fact check

    private static LookupResult Facts(string facts, params ChatItem[] items) => new()
    {
        Request = new LookupRequest(),
        ScopePath = @"D:\Downloads",
        Answer = "",
        FactsText = facts,
        Description = "test",
        Items = items,
    };

    private static readonly ChatItem Installer = new()
    {
        Name = "OllamaSetup.exe", Path = @"D:\Downloads\OllamaSetup.exe", Bytes = 1_943_000_000, SizeText = "1.81 GB",
        Verdict = Verdict.RecommendDelete, VerdictText = "Recommended to delete",
    };

    private static readonly ChatItem Photo = new()
    {
        Name = "wedding.jpg", Path = @"D:\Downloads\wedding.jpg", Bytes = 5_000_000, SizeText = "4.77 MB",
        Verdict = Verdict.KeepPersonal, VerdictText = "Keep (your file)",
    };

    private static readonly LookupResult Sample = Facts(
        "Matching in total: 12 files, 6.64 GB\n1. OllamaSetup.exe — 1.81 GB — PuterScanny: Recommended to delete\n2. wedding.jpg — 4.77 MB — PuterScanny: Keep (your file)\n",
        Installer, Photo);

    [Theory]
    [InlineData("PuterScanny says 6.64 GB can go, starting with OllamaSetup.exe (1.81 GB).")]
    [InlineData("About 6.6 GB in 12 files is marked Recommended to delete; the biggest is OllamaSetup.exe.")]
    [InlineData("OllamaSetup.exe is recommended to delete by PuterScanny, while wedding.jpg is one of your own files to keep.")]
    public void Answers_that_stick_to_the_results_pass(string answer)
    {
        var outcome = AnswerChecker.Check(answer, "what can I delete", [Sample]);
        Assert.True(outcome.Ok, string.Join("; ", outcome.Problems));
    }

    [Theory]
    [InlineData("You could free 9.2 GB in total.")]                        // a size that isn't in the results
    [InlineData("There are 40 files PuterScanny would remove.")]           // a count that isn't in the results
    [InlineData("Start with setup_old.exe, then OllamaSetup.exe.")]        // a file that isn't in the results
    [InlineData("OllamaSetup.exe has been here since 2019.")]               // a year that isn't in the results
    [InlineData("There are no photos in your Downloads.")]                  // "none" when nothing searched for photos
    public void Answers_with_invented_facts_fail(string answer)
    {
        Assert.False(AnswerChecker.Check(answer, "what can I delete", [Sample]).Ok);
    }

    [Fact]
    public void Saying_none_were_found_is_fine_when_a_lookup_searched_and_found_nothing()
    {
        var empty = Facts("Lookup: files from a Mac in Downloads\nFound: 0 items, 0 bytes\n") with { Request = new LookupRequest { Kind = LookupKind.Topic, Topic = "mac" } };
        var outcome = AnswerChecker.Check("I found no files from a Mac in your Downloads.", "any Mac files?", [empty]);
        Assert.True(outcome.Ok, string.Join("; ", outcome.Problems));
    }

    [Fact]
    public void Advice_that_contradicts_PuterScanny_is_removed()
    {
        var outcome = AnswerChecker.Check(
            "OllamaSetup.exe is recommended to delete. You can delete wedding.jpg too. It is 4.77 MB.",
            "what can I delete", [Sample]);
        Assert.DoesNotContain("wedding.jpg too", outcome.Text);
        Assert.Contains("OllamaSetup.exe is recommended to delete.", outcome.Text);

        outcome = AnswerChecker.Check("You should keep OllamaSetup.exe, it is only 1.81 GB. PuterScanny lists it as Recommended to delete.", "q", [Sample]);
        Assert.DoesNotContain("should keep", outcome.Text);
    }
}
