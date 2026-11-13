using System.Runtime.InteropServices;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Signals.SystemRefs;

namespace PuterScanny.Cleanup;

/// <summary>
/// The last line of defence, checked live right before anything is moved, whatever the scan said:
/// places that must never be removed, items that changed since the scan, and anything a running program is using.
/// </summary>
public sealed class SafetyChecks
{
    private readonly List<string> _neverInside = [];
    private readonly HashSet<string> _neverExactly = new(StringComparer.OrdinalIgnoreCase);

    public SafetyChecks(PathTokens tokens, IEnumerable<string>? alsoProtect = null)
    {
        string? T(string n) => tokens.Values.TryGetValue(n, out var v) ? v.TrimEnd('\\') : null;

        // Never remove anything inside these.
        foreach (var p in new[]
        {
            T("windir"), T("programfiles"), T("programfilesx86"),
            T("programdata") is { } pd ? Path.Combine(pd, "Microsoft") : null,
            T("programdata") is { } pd2 ? Path.Combine(pd2, "Package Cache") : null,
            T("systemdrive") is { } sd ? sd + @"\Boot" : null,
            T("systemdrive") is { } sd2 ? sd2 + @"\EFI" : null,
            T("systemdrive") is { } sd3 ? sd3 + @"\Recovery" : null,
        })
        {
            if (p is not null) _neverInside.Add(p + "\\");
        }

        // Never remove these folders themselves (their contents may be cleanable).
        foreach (var p in new[] { T("windir"), T("programfiles"), T("programfilesx86"), T("programdata"), T("users") })
            if (p is not null) _neverExactly.Add(p);
        foreach (var d in DriveInfo.GetDrives()) _neverExactly.Add(d.Name.TrimEnd('\\'));
        foreach (Environment.SpecialFolder f in new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Favorites,
            Environment.SpecialFolder.Recent, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Templates,
        })
        {
            string path = Environment.GetFolderPath(f);
            if (path.Length > 0) _neverExactly.Add(path.TrimEnd('\\'));
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var sub in new[] { "Downloads", "OneDrive", @"AppData", @"AppData\LocalLow", @"AppData\Local\Temp", @"AppData\Local\Packages", @"AppData\Local\Programs" })
            _neverExactly.Add(Path.Combine(profile, sub));
        foreach (var extra in alsoProtect ?? []) _neverInside.Add(extra.TrimEnd('\\') + "\\");
    }

    /// <summary>Why this exact path may never be removed by PuterScanny, or null when it may.</summary>
    public string? Blocked(string path)
    {
        string p = path.TrimEnd('\\');
        if (p.Length <= 3) return "It's a whole drive.";
        if (p.Contains(@"\" + Quarantine.FolderName, StringComparison.OrdinalIgnoreCase) || p.Contains(@"\AppData\Local\PuterScanny", StringComparison.OrdinalIgnoreCase))
            return "It's PuterScanny's own data (manage the quarantine from the Undo & quarantine tab).";
        if (_neverExactly.Contains(p)) return "It's a folder Windows or your account relies on (only things inside it can be cleaned).";
        foreach (string root in _neverInside)
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(p + "\\", root, StringComparison.OrdinalIgnoreCase))
                return "It's inside Windows or installed programs, which PuterScanny never touches.";
        return null;
    }

    /// <summary>Everything that must be true right before removing <paramref name="item"/>. Returns null when it's safe.</summary>
    public string? Check(CleanupItem item)
    {
        if (item.Verdict is Verdict.Required or Verdict.Keep or Verdict.KeepPersonal)
            return "PuterScanny only removes items it rated Review, Safe or Recommended.";
        if (item.Action is not (CleanupAction.Delete or CleanupAction.KeepOneCopy))
            return "This item should be cleaned another way (a Windows tool, its app, or uninstalling).";
        if (Blocked(item.Path) is { } why) return why;

        if (item.IsFolder)
        {
            if (!Directory.Exists(item.Path)) return "It no longer exists.";
            var info = new DirectoryInfo(item.Path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "It's a link to another place, not a real folder.";

            // Re-read the folder now: new or changed files since the scan, in-use files or protected files stop it.
            var files = new List<string>();
            long size = 0;
            DateTime newest = DateTime.MinValue;
            try
            {
                foreach (var f in info.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    files.Add(f.FullName);
                    size += f.Length;
                    if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
                    if (WindowsResourceProtection.IsProtected(f.FullName)) return $"\"{f.Name}\" inside it is protected by Windows.";
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return "Part of it can't be read right now, so it isn't safe to remove as a whole.";
            }
            if (files.Count > item.FileCountAtScan + 5 || newest > item.ModifiedUtcAtScan.AddMinutes(1))
                return "Files inside it changed since the scan. Scan again to get a fresh recommendation.";
            if (size > item.Size * 1.05 + 1024 * 1024)
                return "It grew since the scan. Scan again to get a fresh recommendation.";
            return InUseBy(files) is { } users ? $"In use right now by {users}." : null;
        }
        else
        {
            if (!File.Exists(item.Path)) return "It no longer exists.";
            var info = new FileInfo(item.Path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "It's a link to another place, not a real file.";
            if (info.Length != item.Size || Math.Abs((info.LastWriteTimeUtc - item.ModifiedUtcAtScan).TotalSeconds) > 2)
                return "It changed since the scan. Scan again to get a fresh recommendation.";
            if (WindowsResourceProtection.IsProtected(item.Path)) return "Windows protects this file.";
            return InUseBy([item.Path]) is { } users ? $"In use right now by {users}." : null;
        }
    }

    /// <summary>Asks Windows' Restart Manager which running programs have these files open (read-only, nothing is closed).</summary>
    public static string? InUseBy(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return null;
        var key = new char[33];
        if (RestartManager.RmStartSession(out uint session, 0, key) != 0) return null;
        try
        {
            const int chunk = 500;
            for (int start = 0; start < files.Count; start += chunk)
            {
                string[] part = files.Skip(start).Take(chunk).ToArray();
                if (RestartManager.RmRegisterResources(session, (uint)part.Length, part, 0, IntPtr.Zero, 0, null) != 0) continue;
            }
            uint needed = 0, count = 0, reasons = 0;
            int rc = RestartManager.RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == 0 || needed == 0) return null;
            var infos = new RestartManager.RM_PROCESS_INFO[needed];
            count = needed;
            if (RestartManager.RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return "another program";
            var names = infos.Take((int)count).Select(i => i.strAppName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(3).ToList();
            return names.Count == 0 ? "another program" : string.Join(", ", names);
        }
        finally
        {
            RestartManager.RmEndSession(session);
        }
    }

    private static class RestartManager
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char[] strSessionKey);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
    }
}
