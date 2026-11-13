using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using PuterScanny.Core.Models;

namespace PuterScanny.Signals.Inventory;

/// <summary>Games installed through Steam, Epic Games and GOG, plus Chocolatey and Scoop packages.</summary>
public static class GameLibrariesCollector
{
    public static List<InstalledApp> CollectSteam()
    {
        var apps = new List<InstalledApp>();
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        }
        catch (Exception) { }
        if (steamPath is null)
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }
            catch (Exception) { }
        }
        if (steamPath is null || !Directory.Exists(steamPath)) return apps;

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        string libFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libFile))
        {
            foreach (var (k, v) in Vdf.Flatten(SafeRead(libFile)))
                if (k.EndsWith("/path", StringComparison.OrdinalIgnoreCase)) libraries.Add(v.Replace(@"\\", @"\"));
        }

        foreach (string lib in libraries)
        {
            string steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"); }
            catch (Exception) { continue; }

            foreach (string manifest in manifests)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in Vdf.Flatten(SafeRead(manifest)))
                {
                    // Only top-level "AppState/<key>" values; nested sections reuse names like "name".
                    int slash = k.IndexOf('/');
                    if (slash > 0 && k.IndexOf('/', slash + 1) < 0) values.TryAdd(k[(slash + 1)..], v);
                }
                if (!values.TryGetValue("name", out var name) || !values.TryGetValue("installdir", out var dir)) continue;
                values.TryGetValue("appid", out var appId);
                string root = Path.Combine(steamapps, "common", dir);

                var extras = new List<string>();
                if (appId is not null)
                {
                    foreach (var extra in new[] { Path.Combine(steamapps, "workshop", "content", appId), Path.Combine(steamapps, "shadercache", appId), Path.Combine(steamapps, "compatdata", appId) })
                        if (Directory.Exists(extra)) extras.Add(extra);
                }

                apps.Add(new InstalledApp
                {
                    Name = name,
                    Publisher = "Steam game",
                    Version = values.GetValueOrDefault("buildid"),
                    InstallLocation = root,
                    InstallDate = UnixTime(values.GetValueOrDefault("LastUpdated")),
                    EstimatedSizeBytes = long.TryParse(values.GetValueOrDefault("SizeOnDisk"), out var sz) ? sz : null,
                    Source = AppSource.Steam,
                    UninstallCommand = appId is null ? null : $"steam://uninstall/{appId}",
                    SourceKey = appId,
                    ExtraFolders = extras,
                });
            }
        }
        return apps;
    }

    public static List<InstalledApp> CollectEpic(string? programData)
    {
        var apps = new List<InstalledApp>();
        if (programData is null) return apps;
        string manifests = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return apps;

        foreach (string file in SafeEnumerate(manifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(SafeRead(file));
                var r = doc.RootElement;
                string? name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                string? location = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                if (name is null || location is null) continue;
                // Engine plugins (e.g. Quixel Bridge) record the engine's folder as their location; they are components, not owners.
                bool isPlugin = r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                                && cats.EnumerateArray().Any(c => string.Equals(c.GetString(), "plugins", StringComparison.OrdinalIgnoreCase));
                bool isEngine = r.TryGetProperty("AppCategories", out cats) && cats.ValueKind == JsonValueKind.Array
                                && cats.EnumerateArray().Any(c => string.Equals(c.GetString(), "engines", StringComparison.OrdinalIgnoreCase));
                apps.Add(new InstalledApp
                {
                    Name = name,
                    Publisher = isPlugin ? "Epic Games plugin" : isEngine ? "Epic Games (engine)" : "Epic Games game",
                    IsSystemComponent = isPlugin,
                    Version = r.TryGetProperty("AppVersionString", out var v) ? v.GetString() : null,
                    InstallLocation = location.Replace('/', '\\').TrimEnd('\\'),
                    EstimatedSizeBytes = r.TryGetProperty("InstallSize", out var s) && s.TryGetInt64(out var size) ? size : null,
                    Source = AppSource.EpicGames,
                    UninstallCommand = "Epic Games Launcher",
                    SourceKey = r.TryGetProperty("CatalogItemId", out var c) ? c.GetString() : Path.GetFileNameWithoutExtension(file),
                });
            }
            catch (JsonException) { }
        }
        return apps;
    }

    public static List<InstalledApp> CollectGog()
    {
        var apps = new List<InstalledApp>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (root is null) return apps;
            foreach (string id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id);
                if (k is null) continue;
                string? name = Value(k, "gameName") ?? Value(k, "GAMENAME");
                string? path = Value(k, "path") ?? Value(k, "PATH");
                if (name is null || path is null) continue;
                apps.Add(new InstalledApp
                {
                    Name = name,
                    Publisher = "GOG game",
                    Version = Value(k, "ver") ?? Value(k, "VERSION"),
                    InstallLocation = path.TrimEnd('\\'),
                    Source = AppSource.Gog,
                    UninstallCommand = Value(k, "uninstallCommand"),
                    SourceKey = id,
                });
            }
        }
        catch (Exception) { }
        return apps;
    }

    public static List<InstalledApp> CollectChocolatey(string? programData)
    {
        var apps = new List<InstalledApp>();
        string? chocoRoot = Environment.GetEnvironmentVariable("ChocolateyInstall") ?? (programData is null ? null : Path.Combine(programData, "chocolatey"));
        if (chocoRoot is null) return apps;
        string lib = Path.Combine(chocoRoot, "lib");
        if (!Directory.Exists(lib)) return apps;

        foreach (string pkg in SafeEnumerateDirs(lib))
        {
            string id = Path.GetFileName(pkg);
            string? version = null;
            string? title = null;
            string? nuspec = SafeEnumerate(pkg, "*.nuspec").FirstOrDefault();
            if (nuspec is not null)
            {
                string text = SafeRead(nuspec);
                version = Between(text, "<version>", "</version>");
                title = Between(text, "<title>", "</title>");
            }
            apps.Add(new InstalledApp
            {
                Name = title ?? id,
                Publisher = "Chocolatey package",
                Version = version,
                InstallLocation = pkg,
                Source = AppSource.Chocolatey,
                UninstallCommand = $"choco uninstall {id}",
                SourceKey = id,
            });
        }
        return apps;
    }

    public static List<InstalledApp> CollectScoop(string? programData)
    {
        var apps = new List<InstalledApp>();
        var roots = new List<string>
        {
            Environment.GetEnvironmentVariable("SCOOP") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"),
        };
        if (Environment.GetEnvironmentVariable("SCOOP_GLOBAL") is { } g) roots.Add(g);
        else if (programData is not null) roots.Add(Path.Combine(programData, "scoop"));

        foreach (string root in roots)
        {
            string appsDir = Path.Combine(root, "apps");
            if (!Directory.Exists(appsDir)) continue;
            foreach (string app in SafeEnumerateDirs(appsDir))
            {
                string id = Path.GetFileName(app);
                if (id.Equals("scoop", StringComparison.OrdinalIgnoreCase)) continue;
                string? version = null;
                string manifest = Path.Combine(app, "current", "manifest.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(SafeRead(manifest));
                        version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                    }
                    catch (JsonException) { }
                }
                apps.Add(new InstalledApp
                {
                    Name = id,
                    Publisher = "Scoop package",
                    Version = version,
                    InstallLocation = app,
                    Source = AppSource.Scoop,
                    UninstallCommand = $"scoop uninstall {id}",
                    SourceKey = id,
                });
            }
        }
        return apps;
    }

    private static string? Value(RegistryKey k, string name) => k.GetValue(name) as string is { Length: > 0 } s ? s : null;

    private static string? Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (a < 0) return null;
        a += start.Length;
        int b = text.IndexOf(end, a, StringComparison.OrdinalIgnoreCase);
        return b > a ? text[a..b].Trim() : null;
    }

    private static DateTime? UnixTime(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0 ? DateTime.UnixEpoch.AddSeconds(t) : null;

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>Minimal reader for Valve's KeyValues text format: yields "parent/child/key" → value pairs.</summary>
public static class Vdf
{
    public static IEnumerable<KeyValuePair<string, string>> Flatten(string text)
    {
        var path = new List<string>();
        string? pendingKey = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{')
            {
                path.Add(pendingKey ?? "");
                pendingKey = null;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (path.Count > 0) path.RemoveAt(path.Count - 1);
                pendingKey = null;
                i++;
                continue;
            }
            if (c == '"')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1] == '\\' ? '\\' : text[i + 1]); i += 2; continue; }
                    sb.Append(text[i++]);
                }
                i++;
                string token = sb.ToString();
                if (pendingKey is null) pendingKey = token;
                else
                {
                    yield return new(string.Join('/', path.Append(pendingKey)), token);
                    pendingKey = null;
                }
                continue;
            }
            i++;
        }
    }
}
