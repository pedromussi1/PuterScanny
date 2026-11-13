using PuterScanny.Core.Models;
using PuterScanny.Knowledge;

namespace PuterScanny.Signals.Inventory;

/// <summary>Everything installed on this PC, from every source we know, with ids assigned.</summary>
public sealed class AppInventory
{
    private AppInventory(List<InstalledApp> apps, List<string> problems)
    {
        Apps = apps;
        Problems = problems;
    }

    public IReadOnlyList<InstalledApp> Apps { get; }

    /// <summary>Sources that could not be read (shown in the report so gaps are visible).</summary>
    public IReadOnlyList<string> Problems { get; }

    public static AppInventory Collect(PathTokens tokens, bool includeMsiComponents, IProgress<string>? status, CancellationToken ct)
    {
        var generic = new GenericFolders(tokens);
        string? programData = tokens.Values.TryGetValue("programdata", out var pd) ? pd : null;
        var all = new List<InstalledApp>();
        var problems = new List<string>();

        void Run(string label, Func<IEnumerable<InstalledApp>> collect)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report(label);
            try { all.AddRange(collect()); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{label}: {ex.Message}");
            }
        }

        Run("Reading installed programs…", () => InstalledProgramsCollector.Collect(generic));
        Run("Reading Microsoft Store apps…", StoreAppsCollector.Collect);
        Run("Reading Steam library…", GameLibrariesCollector.CollectSteam);
        Run("Reading Epic Games library…", () => GameLibrariesCollector.CollectEpic(programData));
        Run("Reading GOG library…", GameLibrariesCollector.CollectGog);
        Run("Reading Chocolatey packages…", () => GameLibrariesCollector.CollectChocolatey(programData));
        Run("Reading Scoop packages…", () => GameLibrariesCollector.CollectScoop(programData));

        // Assign ids in a stable order: real programs first, then the rest.
        var ordered = all
            .OrderBy(a => a.IsSystemComponent)
            .ThenBy(a => a.Source)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select((a, i) => a with { Id = i + 1 })
            .ToList();

        if (includeMsiComponents)
        {
            status?.Report("Reading Windows Installer file records…");
            try
            {
                var owned = MsiComponentCollector.Collect(ct, new Progress<int>(n => { if (n > 0) status?.Report($"Reading Windows Installer file records… {n:N0} components"); }));
                var byProductCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < ordered.Count; i++)
                {
                    string? key = ordered[i].SourceKey;
                    if (ordered[i].Source != AppSource.InstalledProgram || key is null) continue;
                    string last = key[(key.LastIndexOf('\\') + 1)..];
                    if (last.StartsWith('{') && last.EndsWith('}')) byProductCode.TryAdd(last.ToUpperInvariant(), i);
                }

                var filesByApp = new Dictionary<int, List<string>>();
                foreach (var (file, products) in owned)
                {
                    // Shared files (e.g. dotnet.exe, registered by every .NET version) go to the newest installed product.
                    int best = -1;
                    foreach (string product in products)
                    {
                        if (!byProductCode.TryGetValue(product, out int index)) continue;
                        if (best < 0 || IsNewer(ordered[index], ordered[best])) best = index;
                    }
                    if (best < 0) continue;
                    if (!filesByApp.TryGetValue(best, out var list)) filesByApp[best] = list = [];
                    list.Add(file);
                }
                foreach (var (index, files) in filesByApp)
                    ordered[index] = ordered[index] with { OwnedFiles = files };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"Windows Installer records: {ex.Message}");
            }
        }

        return new AppInventory(ordered, problems);
    }

    private static bool IsNewer(InstalledApp a, InstalledApp b)
    {
        if (Version.TryParse(a.Version, out var va) && Version.TryParse(b.Version, out var vb) && va != vb) return va > vb;
        return (a.InstallDate ?? DateTime.MinValue) > (b.InstallDate ?? DateTime.MinValue);
    }
}
