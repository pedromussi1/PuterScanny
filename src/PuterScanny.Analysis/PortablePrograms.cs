using System.Text.RegularExpressions;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Analysis;

/// <summary>
/// Programs that run straight from their own folder without being installed (unzipped tools, emulators, game
/// builds…). Windows has no record of them, so they're found from the scan itself: a folder holding a program
/// (.exe) that isn't an installer. The whole folder then belongs to that program, so its parts (data files,
/// disc images, DLLs) are kept together and never offered for deletion one by one.
/// </summary>
public static partial class PortablePrograms
{
    /// <summary>Location kinds where an unknown folder with a program in it is a portable program.</summary>
    private static readonly HashSet<string> AllowedAreas = new(StringComparer.Ordinal) { "user-files", "game", "cloud" };

    [GeneratedRegex(@"setup|install|uninst|unins\d|redist|prereq|bootstrap", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerWords();

    [GeneratedRegex(@"crash|report|sentry|updat|helper|elevat|uninst|setup|install|redist|prereq|bootstrap|launcher|service", RegexOptions.IgnoreCase)]
    private static partial Regex HelperWords();

    [GeneratedRegex(@"(?<![\d.])(\d+\.\d+(?:\.\d+){0,2})(?![\d.])")]
    private static partial Regex VersionNumber();

    /// <summary>A setup program rather than the program itself ("Docker Desktop Installer.exe", "vc_redist.x64.exe"…).</summary>
    public static bool LooksLikeInstaller(string fileName) => InstallerWords().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>
    /// Finds portable programs in a scan. <paramref name="installed"/> are the programs Windows knows about; anything
    /// inside their folders is theirs, not portable. Returned apps have no id yet.
    /// </summary>
    /// <param name="isAppArea">Where installed programs live (default: Program Files, AppData, Windows, ProgramData); tests replace it.</param>
    public static List<InstalledApp> Find(ScanDatabase db, KnowledgeBase kb, PathTokens tokens, IReadOnlyList<InstalledApp> installed,
        Func<string, bool>? isAppArea = null)
    {
        isAppArea ??= Recommender.InAppArea;
        var installedRoots = installed.Select(a => a.InstallLocation).OfType<string>()
            .Select(p => p.TrimEnd('\\')).Where(p => p.Length > 3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matcher = kb.BuildLocationMatcher(tokens);
        var found = new List<InstalledApp>();
        var roots = new List<string>();

        using var read = db.OpenReadConnection();
        using var cmd = read.CreateCommand();
        cmd.CommandText = """
            SELECT p.path, group_concat(e.name, '|'), group_concat(e.size, '|')
            FROM entry e JOIN entry p ON p.id = e.parent_id
            WHERE e.is_dir = 0 AND lower(e.name) LIKE '%.exe'
            GROUP BY p.id ORDER BY p.path
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string folder = r.GetString(0).TrimEnd('\\');
            string[] names = r.GetString(1).Split('|');
            long[] sizes = r.GetString(2).Split('|').Select(s => long.TryParse(s, out var n) ? n : 0).ToArray();
            var programs = names.Select((n, i) => (Name: n, Size: i < sizes.Length ? sizes[i] : 0)).Where(x => !LooksLikeInstaller(x.Name)).ToList();
            if (programs.Count == 0) continue;
            if (!IsCandidateFolder(folder, matcher, installedRoots, roots, isAppArea)) continue;

            var main = PickMain(programs, Path.GetFileName(folder));
            roots.Add(folder);
            found.Add(new InstalledApp
            {
                Name = Path.GetFileNameWithoutExtension(main),
                Version = VersionFrom(Path.GetFileName(folder)) ?? VersionFrom(Path.GetFileName(Path.GetDirectoryName(folder)) ?? ""),
                InstallLocation = folder,
                Source = AppSource.Portable,
                SourceKey = folder,
            });
        }
        return found;
    }

    private static bool IsCandidateFolder(string folder, PathMatcher<LocationRule> matcher, HashSet<string> installedRoots, List<string> accepted,
        Func<string, bool> isAppArea)
    {
        if (folder.Length <= 3) return false;                       // a drive root is never one program
        if (isAppArea(folder)) return false;           // Program Files, AppData, Windows: installed programs' territory
        foreach (string root in accepted)                           // only the top folder of a program (not its bin\ or tools\)
            if (folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;

        var state = matcher.Start;
        LocationRule? rule = null;
        bool matchedHere = false;
        string prefix = "";
        foreach (string seg in folder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            prefix = prefix.Length == 0 ? seg : prefix + "\\" + seg;
            if (installedRoots.Contains(prefix)) return false;      // inside a program Windows knows about
            state = matcher.Advance(state, seg);
            matchedHere = matcher.TryGetBest(state, out var hit);
            if (matchedHere) rule = hit;
        }
        // Downloads, Desktop, Documents themselves are never "a program", and known areas (caches, temp, build
        // output…) keep their own rules.
        if (matchedHere) return false;
        return rule is null || AllowedAreas.Contains(rule.Category);
    }

    /// <summary>The program that names the folder: the one matching the folder's name, else the biggest non-helper.</summary>
    private static string PickMain(List<(string Name, long Size)> programs, string folderName)
    {
        string folder = Simplify(folderName);
        var byName = programs.FirstOrDefault(p =>
        {
            string n = Simplify(Path.GetFileNameWithoutExtension(p.Name));
            return n.Length >= 3 && (folder.StartsWith(n, StringComparison.Ordinal) || n.StartsWith(folder, StringComparison.Ordinal) && folder.Length >= 3);
        });
        if (byName.Name is not null) return byName.Name;
        var real = programs.Where(p => !HelperWords().IsMatch(p.Name)).ToList();
        return (real.Count > 0 ? real : programs).MaxBy(p => p.Size).Name;
    }

    private static string Simplify(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static string? VersionFrom(string name) => VersionNumber().Match(name) is { Success: true } m ? m.Groups[1].Value : null;
}
