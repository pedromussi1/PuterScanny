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
        Assert.StartsWith("Recommendation: Keep", answer.Text);
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

    [Fact]
    public void The_TorrentZIP_date_is_recognised_in_any_time_zone_and_nothing_else_is()
    {
        Assert.True(FileDates.IsTorrentZipDate(new DateTime(1996, 12, 24, 23, 32, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.True(FileDates.IsTorrentZipDate(new DateTime(1996, 12, 24, 22, 32, 0, DateTimeKind.Utc)));
        Assert.False(FileDates.IsTorrentZipDate(new DateTime(1996, 12, 24, 23, 31, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.False(FileDates.IsTorrentZipDate(new DateTime(2024, 12, 24, 23, 32, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.False(FileDates.IsTorrentZipDate(default));
    }

    [Fact]
    public async Task Can_I_delete_it_after_asking_what_something_is_puts_the_recommendation_first()
    {
        var engine = scan.Engine();
        await Ask(engine, "what is OllamaSetup.exe");
        var answer = await Ask(engine, "can I delete it?");
        Assert.StartsWith("Recommendation: ", answer.Text);
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
    public async Task With_the_AI_on_a_description_is_written_by_it_but_a_misreported_recommendation_is_not_shown()
    {
        // The mod download is marked Review: a description saying PuterScanny recommends keeping it must not be shown.
        var wrong = new FakeModel($"BmGame holds game videos. PuterScanny recommends keeping this folder.");
        var answer = await Ask(scan.Engine(wrong), "What is BmGame?");
        Assert.Equal(AnswerSource.LocalAiFallback, answer.Source);
        Assert.Contains("Just drag the BmGame folder", answer.Text);
        Assert.Equal(2, wrong.Calls.Count);
        Assert.Equal(AnswerChecker.DescribeSystem, wrong.Calls[0].System);

        var right = new FakeModel($"BmGame is part of the mod download {ChatScanFixture.Mod}. It holds Bink videos such as 213_Bane_intro.bik, probably the game's cutscenes. PuterScanny marks the mod download Review.");
        answer = await Ask(scan.Engine(right), "What is BmGame?");
        Assert.Equal(AnswerSource.LocalAi, answer.Source);
        Assert.Contains("probably the game's cutscenes", answer.Text);
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

        public Task<string> GenerateAsync(string system, string user, int maxTokens, string? grammar, float temperature, CancellationToken ct)
        {
            Calls.Add((system, user, grammar));
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }
    }

    private const string PlanDelete =
        """{"lookups":[{"lookup":"list","folder":null,"name":null,"kind":"any","filter":"delete","category":null,"unused_days":null,"older_days":null,"min_mb":null,"sort":"size","count":10}]}""";

    [Fact]
    public async Task The_AI_picks_lookups_and_its_checked_answer_is_shown()
    {
        var deletable = scan.All().Where(e => !e.IsDirectory && e.Analysis?.Verdict is Verdict.RecommendDelete or Verdict.SafeToDelete).ToList();
        string total = ByteSize.Format(deletable.Sum(e => e.Size));
        string biggest = deletable.OrderByDescending(e => e.Size).First().Name;
        var model = new FakeModel(PlanDelete, $"PuterScanny says {total} can be deleted here. The biggest is {biggest}.");
        var answer = await Ask(scan.Engine(model), "I'm running out of room, where do I start?");

        Assert.Equal(AnswerSource.LocalAi, answer.Source);
        Assert.Contains(total, answer.Text);
        Assert.NotEmpty(answer.Items);
        // The planning step is held to the lookup grammar; the answer is written from the lookup's results.
        Assert.NotNull(model.Calls[0].Grammar);
        Assert.Contains(biggest, model.Calls[1].User);
        Assert.Null(model.Calls[1].Grammar);
    }

    [Fact]
    public async Task An_AI_answer_with_made_up_numbers_is_never_shown()
    {
        var model = new FakeModel(PlanDelete, "You can free 42 GB by deleting setup.exe and 300 files.", "Still 42 GB, honestly.");
        var answer = await Ask(scan.Engine(model), "I'm running out of room, where do I start?");

        Assert.Equal(AnswerSource.LocalAiFallback, answer.Source);
        Assert.DoesNotContain("42 GB", answer.Text);
        Assert.Contains("in Downloads can be deleted", answer.Text);
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
        string grammar = AiPlanner.Grammar();
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
    public void Answers_with_invented_facts_fail(string answer)
    {
        Assert.False(AnswerChecker.Check(answer, "what can I delete", [Sample]).Ok);
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
