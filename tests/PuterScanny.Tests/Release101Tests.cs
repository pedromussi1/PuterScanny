using PuterScanny.Analysis;
using PuterScanny.Analysis.Reporting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

/// <summary>Portable programs, Nexus mod downloads, installers in Downloads, and "no reason to remove" (version 1.0.1).</summary>
public sealed class DownloadsFolderTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();

    /// <summary>A fake user's Downloads folder, scanned with this PC's rules pointed at it.</summary>
    private static (TempTree Tree, TempTree Out, ScanDatabase Db, string Downloads) ScanDownloads()
    {
        var tree = new TempTree();
        string dl = @"Users\Sam\Downloads\";
        // A portable program that needs its disc image (like retip running Viva Pinata).
        tree.File(dl + @"retip-1.15-win-amd64\retip.exe", 4000);
        tree.File(dl + @"retip-1.15-win-amd64\rexcrashreporter.exe", 1000);
        tree.File(dl + @"retip-1.15-win-amd64\rexruntime.dll", 1000);
        tree.File(dl + @"retip-1.15-win-amd64\Viva Pinata (Disc 1).iso", 50_000);
        tree.File(dl + @"retip-1.15-win-amd64\assets\debug_pack.bin", 9000);
        tree.File(dl + @"retip-1.15-win-amd64\tools\helper.exe", 500);
        // A packaged game build: its engine program is part of the game, not an installer.
        tree.File(dl + @"Proj\Windows\MyGame.exe", 300);
        tree.File(dl + @"Proj\Windows\Engine\Binaries\Win64\UnrealGame-Win64-Shipping.exe", 8000);
        // Installers.
        tree.File(dl + @"OllamaSetup.exe", 7000);
        tree.File(dl + @"Tools\Install Foo (v1.1.4).exe", 3000);
        // Nexus Mods downloads, extracted and archived.
        tree.File(dl + @"Asylum 4K - REMASTERED (Part 1)-36-2-0-1669218064\BmGame\CookedPC\tex.upk", 20_000);
        tree.File(dl + @"DogCompanion.rar-725-1-1-0-1661494986.rar", 2000);
        // Something nothing identifies.
        tree.File(dl + "mystery.qqz", 1234);
        foreach (string f in Directory.EnumerateFiles(tree.Root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-400));
            File.SetCreationTimeUtc(f, DateTime.UtcNow.AddDays(-400));
        }

        var output = new TempTree();
        string db = Path.Combine(output.Root, "scan.db");
        var tokens = new PathTokens(new Dictionary<string, string> { ["users"] = Path.Combine(tree.Root, "Users") });
        new ScanPipeline(Kb, tokens, new AnalysisOptions
            {
                IncludeMsiComponents = false, IdentifyContents = false, VerifySignatures = false, Gentle = false,
                PortableAppAreaOverride = _ => false, // the test folder lives under %TEMP%, inside AppData
            })
            .Run(new ScanOptions { RootPath = Path.Combine(tree.Root, dl), IncludeAlternateStreams = false }, db, null, default);
        return (tree, output, ScanDatabase.Open(db), Path.Combine(tree.Root, dl));
    }

    private static FileSystemEntry Get(ScanDatabase db, string downloads, string relative)
    {
        using var read = db.OpenReadConnection();
        string path = Path.Combine(downloads, relative).TrimEnd('\\');
        return ScanDatabase.ReadAllEntries(read).Single(e => string.Equals(e.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Portable_program_folders_are_kept_whole_with_their_data()
    {
        var (tree, output, db, dl) = ScanDownloads();
        using (tree) using (output) using (db)
        {
            var portable = db.GetApps().Where(a => a.Source == AppSource.Portable).ToList();
            Assert.Equal(["MyGame", "retip"], portable.Select(a => a.Name).Order());
            Assert.Equal("1.15", portable.Single(a => a.Name == "retip").Version);

            var folder = Get(db, dl, "retip-1.15-win-amd64");
            Assert.Equal(Verdict.Keep, folder.Analysis!.Verdict);
            Assert.Equal("Portable program: retip", folder.Analysis.Headline);

            // The disc image the program runs is part of it, never an "old download".
            var iso = Get(db, dl, @"retip-1.15-win-amd64\Viva Pinata (Disc 1).iso");
            Assert.Equal("retip", iso.Analysis!.OwnerAppName);
            Assert.Equal(Protection.UninstallOnly, iso.Analysis.Protection);
            Assert.Equal(Verdict.Keep, iso.Analysis.Verdict);
            Assert.StartsWith("Part of retip", iso.Analysis.Headline);

            foreach (string part in new[] { @"retip-1.15-win-amd64\retip.exe", @"retip-1.15-win-amd64\tools\helper.exe", @"retip-1.15-win-amd64\assets\debug_pack.bin" })
                Assert.Equal(Verdict.Keep, Get(db, dl, part).Analysis!.Verdict);
        }
    }

    [Fact]
    public void Programs_inside_unpacked_folders_are_not_called_installers()
    {
        var (tree, output, db, dl) = ScanDownloads();
        using (tree) using (output) using (db)
        {
            var engine = Get(db, dl, @"Proj\Windows\Engine\Binaries\Win64\UnrealGame-Win64-Shipping.exe");
            Assert.False(engine.Analysis!.Facts.HasFlag(FactFlags.InstallerInDownloads));
            Assert.Equal(Verdict.Keep, engine.Analysis.Verdict);
            Assert.Equal("MyGame", engine.Analysis.OwnerAppName);

            // Real installers still are: directly in Downloads, or named as installers in a subfolder.
            foreach (string installer in new[] { "OllamaSetup.exe", @"Tools\Install Foo (v1.1.4).exe" })
            {
                var a = Get(db, dl, installer).Analysis!;
                Assert.True(a.Facts.HasFlag(FactFlags.InstallerInDownloads), installer);
                Assert.True(a.Verdict is Verdict.SafeToDelete or Verdict.RecommendDelete, $"{installer}: {a.Verdict}");
            }
        }
    }

    [Fact]
    public void Nexus_mod_downloads_are_offered_for_review()
    {
        var (tree, output, db, dl) = ScanDownloads();
        using (tree) using (output) using (db)
        {
            foreach (string mod in new[] { "Asylum 4K - REMASTERED (Part 1)-36-2-0-1669218064", "DogCompanion.rar-725-1-1-0-1661494986.rar" })
            {
                var a = Get(db, dl, mod).Analysis!;
                Assert.Equal(Verdict.Review, a.Verdict);
                Assert.Equal("Mod download (Nexus Mods)", a.Headline);
            }
            var explained = RecommendationExplainer.Explain(db, Kb, Get(db, dl, "Asylum 4K - REMASTERED (Part 1)-36-2-0-1669218064"))!;
            Assert.Contains(explained.Evidence, ev => ev.Text.Contains("mod #36, version 2.0, uploaded 23 Nov 2022"));
        }
    }

    [Fact]
    public void Unknown_items_are_no_reason_to_remove_not_keep()
    {
        var (tree, output, db, dl) = ScanDownloads();
        using (tree) using (output) using (db)
        {
            var a = Get(db, dl, "mystery.qqz").Analysis!;
            Assert.Equal(Verdict.Keep, a.Verdict);
            Assert.True(a.Facts.HasFlag(FactFlags.NoEvidence));
            Assert.Equal("No reason to remove", ReportData.VerdictName(a.Verdict!.Value, a.Facts));
            Assert.False(Get(db, dl, @"retip-1.15-win-amd64\Viva Pinata (Disc 1).iso").Analysis!.Facts.HasFlag(FactFlags.NoEvidence));

            var (files, bytes) = db.GetNoEvidenceStats();
            Assert.True(files >= 1 && bytes >= 1234);
            var report = ReportData.From(db, Kb);
            Assert.Contains(report.Verdicts, v => v.Verdict == "No reason to remove" && v.Bytes >= 1234);
        }
    }
}

public sealed class Release101UnitTests
{
    [Theory]
    [InlineData("Asylum 4K - REMASTERED (Part 1)-36-2-0-1669218064", true, "Asylum 4K - REMASTERED (Part 1)", 36, "2.0")]
    [InlineData("SkyUI_5_2_SE-12604-5-2SE-1606762052.7z", false, "SkyUI_5_2_SE", 12604, "5.2SE")]
    [InlineData("DogCompanion.rar-725-1-1-0-1661494986.rar", false, "DogCompanion.rar", 725, "1.1.0")]
    public void Nexus_download_names_are_read(string name, bool folder, string modName, int id, string version)
    {
        Assert.True(ModDownloads.TryParse(name, folder, out var mod));
        Assert.Equal(modName, mod.Name);
        Assert.Equal(id, mod.ModId);
        Assert.Equal(version.ToLowerInvariant(), mod.Version.ToLowerInvariant());
    }

    [Theory]
    [InlineData("report-2023-01-15.zip", false)]
    [InlineData("backup-1-2-3-9999999999.zip", false)]      // "upload time" in the future
    [InlineData("photo-12-1-0-1669218064.jpg", false)]      // not an archive
    [InlineData("Asylum-36-2-0-1669218064.zip", true)]      // archive as a folder name: no
    public void Other_names_are_not_mod_downloads(string name, bool folder) => Assert.False(ModDownloads.TryParse(name, folder, out _));

    [Theory]
    [InlineData("OllamaSetup.exe", true)]
    [InlineData("Install IK Product Manager (v1.1.4).exe", true)]
    [InlineData("vc_redist.x64.exe", true)]
    [InlineData("UEPrereqSetup_x64.exe", true)]
    [InlineData("unins000.exe", true)]
    [InlineData("retip.exe", false)]
    [InlineData("UnrealGame-Win64-Shipping.exe", false)]
    [InlineData("DS4Windows.exe", false)]
    public void Installer_names(string name, bool installer) => Assert.Equal(installer, PortablePrograms.LooksLikeInstaller(name));

    [Fact]
    public void Only_the_undecided_fallback_is_marked_no_evidence()
    {
        var now = DateTime.UtcNow;
        FileSystemEntry Entry(string path, string? category) => new()
        {
            Id = 2, ParentId = 1, Name = Path.GetFileName(path), Path = path, IsDirectory = false, Depth = 3, Size = 10, TotalSize = 10,
            CreatedUtc = now.AddDays(-100), ModifiedUtc = now.AddDays(-100), Analysis = new AnalysisInfo { Category = category },
        };
        var unknown = Recommender.Evaluate(new EntryFacts { Entry = Entry(@"D:\Stuff\thing.qqz", "other"), NowUtc = now });
        Assert.Equal(Verdict.Keep, unknown.Verdict);
        Assert.True(unknown.NoEvidence);
        var photo = Recommender.Evaluate(new EntryFacts { Entry = Entry(@"D:\Stuff\photo.jpg", "image"), NowUtc = now });
        Assert.Equal(Verdict.KeepPersonal, photo.Verdict);
        Assert.False(photo.NoEvidence);
    }
}
