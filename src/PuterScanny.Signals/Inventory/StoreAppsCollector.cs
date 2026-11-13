using PuterScanny.Core.Models;

namespace PuterScanny.Signals.Inventory;

/// <summary>Microsoft Store / MSIX / AppX packages installed for the current user, with their data folders.</summary>
public static class StoreAppsCollector
{
    public static List<InstalledApp> Collect()
    {
        var apps = new List<InstalledApp>();
        string localPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

        IEnumerable<Windows.ApplicationModel.Package> packages;
        try
        {
            packages = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty);
        }
        catch (Exception)
        {
            return apps; // Package Manager unavailable (e.g. Windows Server Core): no Store apps to report.
        }

        foreach (var pkg in packages)
        {
            try
            {
                if (pkg.IsResourcePackage) continue; // language/scale add-ons of another package

                var id = pkg.Id;
                string? location = null;
                try { location = pkg.InstalledLocation?.Path; }
                catch (Exception) { /* not accessible: fall back below */ }
                if (location is null)
                {
                    try { location = pkg.InstalledPath; } catch (Exception) { }
                }

                string name = SafeGet(() => pkg.DisplayName) is { Length: > 0 } dn && !dn.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? dn : id.Name;
                string? publisher = SafeGet(() => pkg.PublisherDisplayName);
                bool isWindowsPart = SafeGet(() => pkg.SignatureKind.ToString()) == "System";
                string dataFolder = Path.Combine(localPackages, id.FamilyName);

                apps.Add(new InstalledApp
                {
                    Name = name,
                    Publisher = string.IsNullOrWhiteSpace(publisher) || publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : publisher,
                    Version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}",
                    InstallLocation = location,
                    InstallDate = SafeGetDate(() => pkg.InstalledDate.UtcDateTime),
                    Source = AppSource.StoreApp,
                    IsFramework = pkg.IsFramework,
                    IsSystemComponent = isWindowsPart,
                    SourceKey = id.FamilyName,
                    ExtraFolders = Directory.Exists(dataFolder) ? [dataFolder] : [],
                });
            }
            catch (Exception)
            {
                // A broken package registration must not stop the inventory.
            }
        }
        return apps;
    }

    private static string? SafeGet(Func<string> get)
    {
        try { return get(); } catch (Exception) { return null; }
    }

    private static DateTime? SafeGetDate(Func<DateTime> get)
    {
        try
        {
            var d = get();
            return d.Year > 2000 ? d : null;
        }
        catch (Exception) { return null; }
    }
}
