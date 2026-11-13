using System.Globalization;
using Microsoft.Win32;
using PuterScanny.Core.Models;

namespace PuterScanny.Signals.Inventory;

/// <summary>Reads "Apps &amp; features" (the Uninstall registry keys) for 64-bit, 32-bit and per-user programs.</summary>
public static class InstalledProgramsCollector
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Collect(GenericFolders generic)
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;

                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        if (key is null) continue;
                        var app = Read(key, keyName, hive, generic);
                        if (app is null) continue;
                        string dedupe = $"{app.Name}|{app.Version}|{app.InstallLocation}";
                        if (seen.Add(dedupe)) apps.Add(app);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // One unreadable entry should not hide the rest.
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return apps;
    }

    private static InstalledApp? Read(RegistryKey key, string keyName, RegistryHive hive, GenericFolders generic)
    {
        string? name = Str(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;

        string? releaseType = Str(key, "ReleaseType");
        if (releaseType is "Update" or "Hotfix" or "Security Update" or "ServicePack") return null; // patches, not programs

        bool systemComponent = Int(key, "SystemComponent") == 1 || Str(key, "ParentKeyName") is not null;
        string? location = CleanFolder(Str(key, "InstallLocation"));
        if (location is null || generic.IsGeneric(location))
        {
            location = InferFolder(Str(key, "DisplayIcon"), generic) ?? InferFolder(Str(key, "UninstallString"), generic);
        }
        else if (generic.IsGeneric(location))
        {
            location = null;
        }

        DateTime? installed = null;
        if (Str(key, "InstallDate") is { Length: 8 } d &&
            DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            installed = dt.ToUniversalTime();

        long? sizeKb = Int(key, "EstimatedSize");
        return new InstalledApp
        {
            Name = name.Trim(),
            Publisher = Str(key, "Publisher")?.Trim(),
            Version = Str(key, "DisplayVersion")?.Trim(),
            InstallLocation = location,
            InstallDate = installed,
            EstimatedSizeBytes = sizeKb is > 0 ? sizeKb * 1024 : null,
            Source = AppSource.InstalledProgram,
            UninstallCommand = Str(key, "QuietUninstallString") ?? Str(key, "UninstallString"),
            IsSystemComponent = systemComponent,
            IsFramework = IsRuntime(name),
            SourceKey = (hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + UninstallPath + "\\" + keyName,
        };
    }

    /// <summary>Runtimes and frameworks other programs rely on.</summary>
    private static bool IsRuntime(string name) =>
        name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".NET", StringComparison.OrdinalIgnoreCase) && !name.Contains("SDK", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Windows SDK", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DirectX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Uses the folder of the program's icon or uninstaller when it is clearly the program's own folder.</summary>
    private static string? InferFolder(string? commandOrIcon, GenericFolders generic)
    {
        string? exe = ExtractPath(commandOrIcon);
        if (exe is null || exe.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) return null;
        string? dir = Path.GetDirectoryName(exe);
        if (dir is null || generic.IsGeneric(dir) || generic.IsInstallerCache(dir)) return null;
        // An uninstaller often sits one level down ("App\uninstall\unins000.exe"); use the app folder.
        string leaf = Path.GetFileName(dir);
        if (leaf.Equals("uninstall", StringComparison.OrdinalIgnoreCase) || leaf.Equals("uninst", StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir);
        return dir is null || generic.IsGeneric(dir) ? null : dir;
    }

    internal static string? ExtractPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (v.StartsWith('"'))
        {
            int end = v.IndexOf('"', 1);
            v = end > 1 ? v[1..end] : v.Trim('"');
        }
        else
        {
            int exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) v = v[..(exe + 4)];
            int comma = v.LastIndexOf(',');
            if (comma > 2) v = v[..comma];
        }
        v = Environment.ExpandEnvironmentVariables(v);
        return Path.IsPathFullyQualified(v) ? v : null;
    }

    internal static string? CleanFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).TrimEnd('\\');
        return Path.IsPathFullyQualified(p) && p.Length > 3 ? p : null;
    }

    private static string? Str(RegistryKey key, string name) =>
        key.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static long? Int(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int i => (uint)i,
        long l => l,
        string s when long.TryParse(s, out var v) => v,
        _ => null,
    };
}
