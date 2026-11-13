using PuterScanny.Knowledge;

namespace PuterScanny.Signals.Inventory;

/// <summary>
/// Folders that are shared by many programs, so they must never be treated as one program's install folder
/// (some installers wrongly register "C:\Program Files" or a user's AppData as their location).
/// </summary>
public sealed class GenericFolders
{
    private readonly HashSet<string> _generic = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _installerCaches = [];

    public GenericFolders(PathTokens tokens)
    {
        string? T(string name) => tokens.Values.TryGetValue(name, out var v) ? v : null;
        string? windir = T("windir"), pf = T("programfiles"), pf86 = T("programfilesx86"), pd = T("programdata"), users = T("users"), sd = T("systemdrive");

        foreach (var p in new[]
        {
            windir, pf, pf86, pd, users, sd,
            windir is null ? null : Path.Combine(windir, "System32"),
            windir is null ? null : Path.Combine(windir, "SysWOW64"),
            pf is null ? null : Path.Combine(pf, "Common Files"),
            pf86 is null ? null : Path.Combine(pf86, "Common Files"),
            pf is null ? null : Path.Combine(pf, "WindowsApps"),
            pd is null ? null : Path.Combine(pd, "Microsoft"),
            pd is null ? null : Path.Combine(pd, "Package Cache"),
        })
        {
            if (p is not null) _generic.Add(p.TrimEnd('\\'));
        }

        foreach (var d in DriveInfo.GetDrives()) _generic.Add(d.Name.TrimEnd('\\'));

        if (users is not null && Directory.Exists(users))
        {
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(users))
                {
                    _generic.Add(profile);
                    foreach (var sub in new[] { "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", @"AppData\Local\Programs", @"AppData\Local\Temp",
                                                "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos", "OneDrive" })
                        _generic.Add(Path.Combine(profile, sub));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (windir is not null)
        {
            _installerCaches.Add(Path.Combine(windir, "Installer"));
            _installerCaches.Add(Path.Combine(windir, "Temp"));
        }
        if (pd is not null) _installerCaches.Add(Path.Combine(pd, "Package Cache"));
    }

    public bool IsGeneric(string path) => _generic.Contains(path.TrimEnd('\\'));

    public bool IsInstallerCache(string path) =>
        _installerCaches.Any(c => path.StartsWith(c, StringComparison.OrdinalIgnoreCase)) ||
        path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase);
}
