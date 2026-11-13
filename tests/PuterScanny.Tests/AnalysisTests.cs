using PuterScanny.Analysis;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

public sealed class AnalysisPipelineTests
{
    [Fact]
    public void Scan_and_analysis_label_files_and_known_locations()
    {
        using var tree = new TempTree();   // lives under %TEMP%, a known location
        tree.File(@"site\node_modules\left-pad\index.js", 120);
        File.WriteAllText(Path.Combine(tree.Root, "readme.md"), "# Hello\r\n");
        File.WriteAllBytes(Path.Combine(tree.Root, "picture.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);

        using var dbFolder = new TempTree();
        string dbPath = Path.Combine(dbFolder.Root, "scan.db");
        var kb = KnowledgeBase.LoadDefault();
        // Windows Installer records are machine-wide and slow; they are covered by the real-drive check instead.
        var pipeline = new ScanPipeline(kb, PathTokens.ForThisMachine(), new AnalysisOptions { IncludeMsiComponents = false });

        var summary = pipeline.Run(new ScanOptions { RootPath = tree.Root }, dbPath, null, default);
        Assert.Equal(ScanStatus.Completed, summary.Status);

        using var db = ScanDatabase.Open(dbPath);
        Assert.Equal("complete", db.GetMeta("analysis.status"));

        var root = db.GetRoot()!;
        Assert.Equal("user.temp", root.Analysis!.LocationRuleId);

        var children = db.GetChildren(root.Id);
        var png = children.Single(c => c.Name == "picture.png");
        Assert.Equal("PNG image", png.Analysis!.TypeLabel);
        Assert.Equal("image", png.Analysis.Category);
        Assert.Equal(ReadStatus.Read, png.Analysis.ReadStatus);

        var md = children.Single(c => c.Name == "readme.md");
        Assert.Equal("Markdown document", md.Analysis!.TypeLabel);

        var site = children.Single(c => c.Name == "site");
        var nodeModules = db.GetChildren(site.Id).Single();
        Assert.Equal("dev.node_modules", nodeModules.Analysis!.LocationRuleId);
        var leftPad = db.GetChildren(nodeModules.Id).Single();
        Assert.Equal("dev.node_modules", leftPad.Analysis!.LocationRuleId); // inherited by everything inside

        Assert.NotEmpty(db.GetApps());
        Assert.Contains(db.GetCategoryStats(), c => c.Category == "image");
        Assert.Contains(db.GetExtensionStats(), x => x.Extension == ".png" && x.Files == 1);
    }

    [Fact]
    public void Files_inside_an_installed_program_folder_belong_to_it()
    {
        // Pick any installed program whose folder exists and scan just that folder.
        var kb = KnowledgeBase.LoadDefault();
        var tokens = PathTokens.ForThisMachine();
        var apps = PuterScanny.Signals.Inventory.InstalledProgramsCollector.Collect(new PuterScanny.Signals.Inventory.GenericFolders(tokens));
        var app = apps.FirstOrDefault(a => a.InstallLocation is { } l && Directory.Exists(l) && !a.IsSystemComponent
                                            && Directory.EnumerateFiles(l).Any() && Directory.EnumerateFileSystemEntries(l, "*", SearchOption.AllDirectories).Take(3000).Count() < 3000);
        if (app is null) return;

        using var dbFolder = new TempTree();
        string dbPath = Path.Combine(dbFolder.Root, "scan.db");
        new ScanPipeline(kb, tokens, new AnalysisOptions { IncludeMsiComponents = false, IdentifyContents = false })
            .Run(new ScanOptions { RootPath = app.InstallLocation! }, dbPath, null, default);

        using var db = ScanDatabase.Open(dbPath);
        var root = db.GetRoot()!;
        Assert.NotNull(root.Analysis!.OwnerAppName);
        Assert.True(root.Analysis.OwnerIsExact);
        var file = db.GetLargestFiles(1).Single();
        Assert.Equal(root.Analysis.OwnerAppName, file.Analysis!.OwnerAppName);
        Assert.False(file.Analysis.OwnerIsExact);
    }
}
