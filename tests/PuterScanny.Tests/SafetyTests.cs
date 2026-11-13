using PuterScanny.Analysis;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Signals.SystemRefs;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

/// <summary>Turning the command lines Windows stores into real file paths.</summary>
public sealed class CommandPathTests
{
    private const string Windir = @"C:\Windows";

    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows\System32\drivers\ACPI.sys",
        @"C:\Windows\System32\svchost.exe",
        @"C:\Windows\System32\rundll32.exe",
        @"C:\Windows\System32\wscript.exe",
        @"C:\Program Files\App\app.exe",
        @"C:\Program Files\App Two\helper.dll",
        @"C:\Scripts\nightly.vbs",
        @"D:\Tools\tool.exe",
    };

    private static List<string> X(string cmd) => CommandPaths.Extract(cmd, Windir, Files.Contains);

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\ACPI.sys", @"C:\Windows\System32\drivers\ACPI.sys")]
    [InlineData(@"System32\drivers\ACPI.sys", @"C:\Windows\System32\drivers\ACPI.sys")]
    [InlineData(@"\??\C:\Windows\System32\drivers\ACPI.sys", @"C:\Windows\System32\drivers\ACPI.sys")]
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p", @"C:\Windows\System32\svchost.exe")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --background", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe /minimized", @"C:\Program Files\App\app.exe")]
    [InlineData(@"svchost.exe", @"C:\Windows\System32\svchost.exe")]
    [InlineData(@"D:\Tools\tool", @"D:\Tools\tool.exe")]
    public void The_program_is_found(string command, string expected)
    {
        Assert.Equal(expected, X(command).First(), ignoreCase: true);
    }

    [Fact]
    public void Files_in_arguments_are_found_too()
    {
        var rundll = X(@"rundll32.exe ""C:\Program Files\App Two\helper.dll"",Start");
        Assert.Equal([@"C:\Windows\System32\rundll32.exe", @"C:\Program Files\App Two\helper.dll"], rundll);

        var script = X(@"wscript.exe //B C:\Scripts\nightly.vbs");
        Assert.Contains(@"C:\Scripts\nightly.vbs", script);
    }

    [Fact]
    public void Nothing_is_invented()
    {
        Assert.Empty(X(@"C:\Gone\missing.exe -x"));
        Assert.Empty(X(""));
        Assert.Empty(X("   "));
    }
}

/// <summary>
/// The safety net: PuterScanny must never treat anything Windows or a program needs as unprotected.
/// These tests scan real folders of this PC.
/// </summary>
[Trait("Category", "Safety")]
public sealed class SafetyTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();
    private static readonly string Windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ScanDatabase ScanAndOpen(string root, TempTree dbFolder, bool lookInside = false)
    {
        string db = Path.Combine(dbFolder.Root, "scan.db");
        new ScanPipeline(Kb, PathTokens.ForThisMachine(),
                new AnalysisOptions { IncludeMsiComponents = false, IdentifyContents = lookInside, VerifySignatures = false })
            .Run(new ScanOptions { RootPath = root, IncludeAlternateStreams = false }, db, null, default);
        return ScanDatabase.Open(db);
    }

    private static List<(string Path, bool IsDir, Protection Level, string? Rule)> AllEntries(ScanDatabase db)
    {
        using var c = db.OpenReadConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT e.path, e.is_dir, a.protection, a.location_rule FROM entry e JOIN analysis a ON a.entry_id = e.id";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, bool, Protection, string?)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1) != 0, (Protection)r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    [Fact]
    public void Every_driver_file_is_required()
    {
        using var dbFolder = new TempTree();
        using var db = ScanAndOpen(Path.Combine(Windir, "System32", "drivers"), dbFolder);
        var files = AllEntries(db).Where(e => !e.IsDir).ToList();
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.True(f.Level == Protection.Required, $"{f.Path} is only {f.Level}"));
        Assert.Equal(Protection.Required, db.GetRoot()!.Analysis!.ContainedProtection);
    }

    [Fact]
    public void Inside_System32_only_explicitly_known_clean_up_locations_are_unprotected()
    {
        using var dbFolder = new TempTree();
        using var db = ScanAndOpen(Path.Combine(Windir, "System32"), dbFolder);
        var entries = AllEntries(db);
        Assert.True(entries.Count > 5_000);

        // Anything below "Required" must sit in a location the knowledge base explicitly describes as not protected
        // (logs, temporary files, crash dumps…). An unknown corner of System32 always stays protected.
        var weak = entries.Where(e => e.Level < Protection.Required).ToList();
        Assert.All(weak, e =>
        {
            var rule = Kb.GetRule(e.Rule);
            Assert.True(rule is not null && rule.Protection < Protection.Required,
                $"{e.Path} is {e.Level} under rule {e.Rule ?? "(none)"}");
        });

        // Core files are protected for several independent reasons.
        string kernel32 = Path.Combine(Windir, "System32", "kernel32.dll");
        var k32 = db.GetLargestFiles(int.MaxValue).Single(f => string.Equals(f.Path, kernel32, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Protection.Required, k32.Analysis!.Protection);
        var reasons = db.GetReferences(k32.Id).Select(r => r.Kind).ToHashSet();
        Assert.Contains(ReferenceKind.WindowsProtected, reasons);
        Assert.Contains(ReferenceKind.LoadedByProgram, reasons); // every running program loads kernel32
    }

    [Fact]
    public void Services_drivers_fonts_and_running_programs_are_found()
    {
        var refs = SystemReferenceCollector.Collect(Windir, [], null, default);

        // A few service registry keys are locked for normal users; that must never stop the rest being read.
        Assert.DoesNotContain(refs.Problems, p => p.Contains("services", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs.Problems, p => p.Contains("components", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(refs.ByPath, kv => kv.Value.Any(r => r.Kind == ReferenceKind.Service && r.Level == Protection.Required)
                                           && kv.Key.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(refs.ByPath, kv => kv.Value.Any(r => r.Kind == ReferenceKind.Driver) && kv.Key.EndsWith(".sys", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(refs.ByPath, kv => kv.Value.Any(r => r.Kind == ReferenceKind.Font));
        Assert.Contains(refs.ByPath, kv => kv.Value.Any(r => r.Kind == ReferenceKind.ComComponent));
        Assert.Contains(refs.ByPath, kv => kv.Value.Any(r => r.Kind == ReferenceKind.FileAssociation));

        string self = Environment.ProcessPath!;
        Assert.True(refs.TryGet(self, out var mine), "this test process should be listed as running");
        Assert.Contains(mine, r => r.Kind == ReferenceKind.RunningProgram && r.Level == Protection.InUse);
    }

    [Fact]
    public void Files_a_running_program_has_loaded_are_in_use_and_their_folder_is_kept()
    {
        // This test process has loaded the SQLite engine from its own folder.
        string folder = AppContext.BaseDirectory.TrimEnd('\\');
        using var dbFolder = new TempTree();
        using var db = ScanAndOpen(folder, dbFolder);
        var sqlite = AllEntries(db).Single(e => e.Path.EndsWith("Microsoft.Data.Sqlite.dll", StringComparison.OrdinalIgnoreCase));
        Assert.True(sqlite.Level >= Protection.InUse, $"Microsoft.Data.Sqlite.dll is only {sqlite.Level}");
        Assert.True(db.GetRoot()!.Analysis!.ContainedProtection >= Protection.InUse);
    }

    [Fact]
    public void An_installed_programs_files_are_uninstall_only_or_stronger()
    {
        var tokens = PathTokens.ForThisMachine();
        var apps = PuterScanny.Signals.Inventory.InstalledProgramsCollector.Collect(new PuterScanny.Signals.Inventory.GenericFolders(tokens));
        var app = apps.FirstOrDefault(a => a.InstallLocation is { } l && Directory.Exists(l) && !a.IsSystemComponent
            && Directory.EnumerateFiles(l).Any()
            && Directory.EnumerateFileSystemEntries(l, "*", SearchOption.AllDirectories).Take(3000).Count() < 3000);
        if (app is null) return;

        using var dbFolder = new TempTree();
        using var db = ScanAndOpen(app.InstallLocation!, dbFolder);
        Assert.All(AllEntries(db).Where(e => !e.IsDir), f =>
            Assert.True(f.Level >= Protection.UninstallOnly, $"{f.Path} is only {f.Level}"));
    }

    [Fact]
    public void Ordinary_temporary_files_get_no_protection()
    {
        using var tree = new TempTree();
        tree.File(@"old\leftover.tmp", 100);
        using var dbFolder = new TempTree();
        using var db = ScanAndOpen(tree.Root, dbFolder, lookInside: true);
        var tmp = AllEntries(db).Single(e => e.Path.EndsWith("leftover.tmp"));
        Assert.Equal(Protection.None, tmp.Level);
        Assert.Equal(Protection.None, db.GetRoot()!.Analysis!.ContainedProtection);
    }

    [Fact]
    public void A_file_locked_by_another_program_is_in_use()
    {
        using var tree = new TempTree();
        string locked = tree.File("locked.dat", 100);
        using var dbFolder = new TempTree();
        // Open the file exclusively, as a running program would (no sharing at all).
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var db = ScanAndOpen(tree.Root, dbFolder, lookInside: true);
            var entry = db.GetChildren(db.GetRoot()!.Id).Single();
            Assert.Equal(ReadStatus.InUse, entry.Analysis!.ReadStatus);
            Assert.Equal(Protection.InUse, entry.Analysis.Protection);
            Assert.Contains(db.GetReferences(entry.Id), r => r.Kind == ReferenceKind.Locked);
        }
    }
}
