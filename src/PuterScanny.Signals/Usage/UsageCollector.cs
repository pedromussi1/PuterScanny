using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PuterScanny.Core.Models;
using PuterScanny.Signals.Inventory;

namespace PuterScanny.Signals.Usage;

/// <summary>Usage history, keyed by file/folder path and by installed program.</summary>
public sealed class UsageIndex
{
    private readonly Dictionary<string, List<UsageEvent>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, List<UsageEvent>> _byApp = new();

    public IReadOnlyDictionary<string, List<UsageEvent>> ByPath => _byPath;
    public IReadOnlyDictionary<long, List<UsageEvent>> ByApp => _byApp;
    public List<UsageSourceStatus> Sources { get; } = [];

    /// <summary>True when Windows keeps "last accessed" dates up to date on this PC.</summary>
    public bool LastAccessReliable { get; set; }

    public void AddPath(string path, UsageEvent e)
    {
        string key = path.TrimEnd('\\');
        if (key.Length < 3) return;
        if (!_byPath.TryGetValue(key, out var list)) _byPath[key] = list = [];
        if (list.Count < 30) list.Add(e);
    }

    public void AddApp(long appId, UsageEvent e)
    {
        if (!_byApp.TryGetValue(appId, out var list)) _byApp[appId] = list = [];
        list.Add(e);
    }

    public bool TryGet(string path, out List<UsageEvent> events) => _byPath.TryGetValue(path, out events!);

    /// <summary>Combines events: the latest date, and the highest count and active time reported by any one source.</summary>
    public static UsageSummary Summarize(IEnumerable<UsageEvent> events)
    {
        DateTime? last = null;
        int count = 0;
        TimeSpan active = TimeSpan.Zero;
        UsageSource? best = null;
        foreach (var e in events)
        {
            if (e.LastUsedUtc is { } t && (last is null || t > last))
            {
                last = t;
                best = e.Source;
            }
            if (e.Count is int c && c > count) count = c;
            if (e.ActiveTime is { } a && a > active) active = a; // max, not sum: a program and its shortcut record the same use
            best ??= e.Source;
        }
        return new UsageSummary(last, count, active, best);
    }
}

/// <summary>
/// Gathers how recently and how often files and programs were used, from records Windows and apps keep for the
/// current user. Only reads; nothing is changed. Sources that need administrator rights are reported as unavailable.
/// </summary>
public static class UsageCollector
{
    public static UsageIndex Collect(IReadOnlyList<InstalledApp> apps, IProgress<string>? status, CancellationToken ct)
    {
        var index = new UsageIndex();

        void Step(UsageSource source, string label, Func<UsageIndex, int> collect)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report(label);
            try
            {
                int n = collect(index);
                index.Sources.Add(new UsageSourceStatus(source, true, n, ""));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                index.Sources.Add(new UsageSourceStatus(source, false, 0, ex.Message));
            }
        }

        Step(UsageSource.ProgramLaunches, "Reading which programs you start…", i => ProgramLaunches(i, apps));
        Step(UsageSource.RecentItems, "Reading your recent files…", RecentItems);
        Step(UsageSource.JumpLists, "Reading apps' recent and frequent lists…", JumpListItems);
        Step(UsageSource.OfficeRecent, "Reading Office's recent documents…", OfficeRecent);
        Step(UsageSource.Steam, "Reading Steam play history…", i => SteamPlayHistory(i, apps));

        index.LastAccessReliable = LastAccessIsUpdated();
        index.Sources.Add(new UsageSourceStatus(UsageSource.LastAccess, index.LastAccessReliable, 0,
            index.LastAccessReliable ? "Windows updates \"last accessed\" dates on this PC (to within about an hour)." : "Windows does not update \"last accessed\" dates on this PC, so they are ignored."));

        bool admin = PuterScanny.Signals.Native.AdminCheck.IsElevated();
        if (admin)
        {
            Step(UsageSource.Prefetch, "Reading Windows' record of program runs (Prefetch)…", Prefetch);
            Step(UsageSource.BackgroundActivity, "Reading Windows' record of last program runs (BAM)…", Bam);
        }
        else
        {
            index.Sources.Add(new UsageSourceStatus(UsageSource.Prefetch, false, 0,
                "Windows' record of how often each program ran (Prefetch): needs administrator rights (use Deep scan)."));
            index.Sources.Add(new UsageSourceStatus(UsageSource.BackgroundActivity, false, 0,
                "Windows' record of when each program last ran (BAM): needs administrator rights (use Deep scan)."));
        }
        index.Sources.Add(new UsageSourceStatus(UsageSource.Amcache, false, 0,
            "Windows' program inventory (Amcache): Windows keeps this file locked at all times, and reading it would mean copying it through a shadow copy, so it isn't read. Prefetch and BAM cover the same program runs."));
        return index;
    }

    // ------------------------------------------------------------------ administrator-only records

    private static int Prefetch(UsageIndex index)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        if (!Directory.Exists(folder)) return 0;
        var serials = PrefetchReader.VolumeSerials();
        int found = 0;
        foreach (string pf in Directory.EnumerateFiles(folder, "*.pf"))
        {
            if (PrefetchReader.ReadFile(pf, serials) is not { FullPath: { } path } entry) continue;
            DateTime? last = entry.RunTimesUtc.Count > 0 ? entry.RunTimesUtc[0] : null;
            string detail = entry.RunCount > 0
                ? $"Ran {entry.RunCount:N0} time{(entry.RunCount == 1 ? "" : "s")} (Windows' Prefetch record)"
                : "Ran (Windows' Prefetch record)";
            index.AddPath(path, new UsageEvent(UsageSource.Prefetch, last, entry.RunCount > 0 ? entry.RunCount : null, null, detail));
            found++;
        }
        return found;
    }

    private static int Bam(UsageIndex index)
    {
        int found = 0;
        foreach (var (path, when) in BamReader.Read())
        {
            index.AddPath(path, new UsageEvent(UsageSource.BackgroundActivity, when, null, null, "Last run (Windows' background activity record)"));
            found++;
        }
        return found;
    }

    // ------------------------------------------------------------------ UserAssist

    private static readonly Guid ExecutablesGuid = new("CEBFF5CD-ACE2-4F4F-9178-9926F41749EA");
    private static readonly Guid ShortcutsGuid = new("F4E57C4B-2036-45F0-A9AB-443BCFE33D9F");

    private static int ProgramLaunches(UsageIndex index, IReadOnlyList<InstalledApp> apps)
    {
        var storeApps = apps.Where(a => a.Source == AppSource.StoreApp && a.SourceKey is not null)
            .GroupBy(a => a.SourceKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        int found = 0;

        foreach (var guid in new[] { ExecutablesGuid, ShortcutsGuid })
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{{{guid.ToString().ToUpperInvariant()}}}\Count");
            if (key is null) continue;
            foreach (string encoded in key.GetValueNames())
            {
                if (key.GetValue(encoded) is not byte[] { Length: >= 68 } data) continue;
                string name = Rot13(encoded);
                if (name.StartsWith("UEME_", StringComparison.Ordinal)) continue;

                int runs = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                int focusMs = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
                DateTime? last = FileTime(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(60)));
                TimeSpan? active = focusMs > 0 ? TimeSpan.FromMilliseconds(focusMs) : null;
                if (runs <= 0 && last is null && active is null) continue;

                var ev = new UsageEvent(UsageSource.ProgramLaunches, last, runs > 0 ? runs : null, active,
                    runs > 0 ? $"Started {runs:N0} time{(runs == 1 ? "" : "s")} from the Start menu, taskbar or File Explorer" : "Used from the Start menu, taskbar or File Explorer");

                // Store apps are recorded by their app id ("Family!App").
                int bang = name.IndexOf('!');
                if (bang > 0 && !name.Contains('\\'))
                {
                    if (storeApps.TryGetValue(name[..bang], out long appId))
                    {
                        index.AddApp(appId, ev);
                        found++;
                    }
                    continue;
                }

                string? path = ExpandKnownFolder(name);
                if (path is null) continue;
                index.AddPath(path, ev);
                found++;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellLink.ReadTarget(path) is { } target)
                    index.AddPath(target, ev with { Detail = ev.Detail + " (through a shortcut)" });
            }
        }
        return found;
    }

    private static string Rot13(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            char ch = c[i];
            if (ch is >= 'a' and <= 'z') c[i] = (char)('a' + (ch - 'a' + 13) % 26);
            else if (ch is >= 'A' and <= 'Z') c[i] = (char)('A' + (ch - 'A' + 13) % 26);
        }
        return new string(c);
    }

    /// <summary>"{6D809377-…}\7-Zip\7zFM.exe" → "C:\Program Files\7-Zip\7zFM.exe".</summary>
    internal static string? ExpandKnownFolder(string name)
    {
        if (name.StartsWith('{'))
        {
            int close = name.IndexOf('}');
            if (close < 0 || !Guid.TryParse(name[..(close + 1)], out var folderId)) return null;
            string? folder = Native.KnownFolders.GetPath(folderId);
            if (folder is null) return null;
            string rest = name[(close + 1)..].TrimStart('\\');
            return rest.Length == 0 ? folder : Path.Combine(folder, rest);
        }
        string expanded = Environment.ExpandEnvironmentVariables(name);
        return Path.IsPathFullyQualified(expanded) ? expanded : null;
    }

    // ------------------------------------------------------------------ Recent items & jump lists

    private static int RecentItems(UsageIndex index)
    {
        string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recent)) return 0;
        int found = 0;
        foreach (string lnk in Directory.EnumerateFiles(recent, "*.lnk"))
        {
            string? target = ShellLink.ReadTarget(lnk);
            if (target is null) continue;
            // Windows rewrites the shortcut each time the item is opened, so its date is the last time it was opened.
            DateTime opened = File.GetLastWriteTimeUtc(lnk);
            index.AddPath(target, new UsageEvent(UsageSource.RecentItems, opened, null, null, "Opened recently (Windows' Recent items)"));
            found++;
        }
        return found;
    }

    private static int JumpListItems(UsageIndex index)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Recent), "AutomaticDestinations");
        if (!Directory.Exists(folder)) return 0;
        int found = 0;
        foreach (string file in Directory.EnumerateFiles(folder, "*.automaticDestinations-ms"))
        {
            foreach (var item in JumpLists.Read(file))
            {
                string app = JumpLists.AppName(item.AppId);
                string detail = item.AccessCount is int n
                    ? $"Opened {n:N0} time{(n == 1 ? "" : "s")} with {app} (from its jump list){(item.Pinned ? ", pinned" : "")}"
                    : $"In {app}'s list of recent files{(item.Pinned ? " (pinned)" : "")}";
                index.AddPath(item.Path, new UsageEvent(UsageSource.JumpLists, item.LastAccessUtc, item.AccessCount, null, detail));
                found++;
            }
        }
        return found;
    }

    // ------------------------------------------------------------------ Office

    private static readonly Regex OfficeItem = new(@"\[T([0-9A-F]{16})\].*?\*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static int OfficeRecent(UsageIndex index)
    {
        int found = 0;
        using var office = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
        if (office is null) return 0;
        foreach (string version in office.GetSubKeyNames().Where(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            foreach (string app in new[] { "Word", "Excel", "PowerPoint", "Access", "Visio", "Publisher", "OneNote" })
            {
                using var mru = office.OpenSubKey($@"{version}\{app}\User MRU");
                if (mru is null) continue;
                foreach (string account in mru.GetSubKeyNames())
                {
                    using var files = mru.OpenSubKey($@"{account}\File MRU");
                    if (files is null) continue;
                    foreach (string item in files.GetValueNames())
                    {
                        if (files.GetValue(item) is not string value) continue;
                        var m = OfficeItem.Match(value);
                        if (!m.Success) continue;
                        DateTime? when = FileTime(long.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index.AddPath(m.Groups[2].Value, new UsageEvent(UsageSource.OfficeRecent, when, null, null, $"In {app}'s recent documents"));
                        found++;
                    }
                }
            }
        }
        return found;
    }

    // ------------------------------------------------------------------ Steam

    private static int SteamPlayHistory(UsageIndex index, IReadOnlyList<InstalledApp> apps)
    {
        var games = apps.Where(a => a.Source == AppSource.Steam && a.SourceKey is not null)
            .GroupBy(a => a.SourceKey!).ToDictionary(g => g.Key, g => g.First().Id);
        if (games.Count == 0) return 0;

        string? steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (steam is null) return 0;
        string userdata = Path.Combine(steam.Replace('/', '\\'), "userdata");
        if (!Directory.Exists(userdata)) return 0;

        var lastPlayed = new Dictionary<string, DateTime>();
        var minutes = new Dictionary<string, int>();
        foreach (string config in Directory.EnumerateFiles(userdata, "localconfig.vdf", SearchOption.AllDirectories))
        {
            foreach (var (k, v) in Vdf.Flatten(File.ReadAllText(config)))
            {
                // ".../Software/Valve/Steam/apps/<appid>/LastPlayed"
                int apps_ = k.IndexOf("/apps/", StringComparison.OrdinalIgnoreCase);
                if (apps_ < 0) continue;
                string[] rest = k[(apps_ + 6)..].Split('/');
                if (rest.Length != 2) continue;
                string appId = rest[0];
                if (rest[1].Equals("LastPlayed", StringComparison.OrdinalIgnoreCase) && long.TryParse(v, out long unix) && unix > 0)
                {
                    var t = DateTime.UnixEpoch.AddSeconds(unix);
                    if (!lastPlayed.TryGetValue(appId, out var prev) || t > prev) lastPlayed[appId] = t;
                }
                else if (rest[1].Equals("Playtime", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int m))
                {
                    minutes[appId] = Math.Max(m, minutes.GetValueOrDefault(appId));
                }
            }
        }

        int found = 0;
        foreach (var (appId, gameId) in games)
        {
            bool played = lastPlayed.TryGetValue(appId, out var when);
            int mins = minutes.GetValueOrDefault(appId);
            if (!played && mins == 0) continue;
            string detail = mins > 0 ? $"Played for {FormatMinutes(mins)} in total (Steam)" : "Played (Steam)";
            index.AddApp(gameId, new UsageEvent(UsageSource.Steam, played ? when : null, null, mins > 0 ? TimeSpan.FromMinutes(mins) : null, detail));
            found++;
        }
        return found;
    }

    private static string FormatMinutes(int m) => m < 60 ? $"{m} min" : $"{m / 60.0:0.#} hours";

    // ------------------------------------------------------------------ helpers

    /// <summary>NtfsDisableLastAccessUpdate: 0/2 (or 0x80000000/0x80000002) mean Windows keeps last-access dates.</summary>
    private static bool LastAccessIsUpdated()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
        if (key?.GetValue("NtfsDisableLastAccessUpdate") is not int raw) return false;
        return ((uint)raw & 0x1) == 0;
    }

    private static DateTime? FileTime(long ft)
    {
        if (ft <= 0) return null;
        try
        {
            var t = DateTime.FromFileTimeUtc(ft);
            return t.Year >= 2000 && t <= DateTime.UtcNow.AddDays(1) ? t : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
