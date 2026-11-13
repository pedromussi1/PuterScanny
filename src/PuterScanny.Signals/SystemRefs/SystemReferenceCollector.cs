using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PuterScanny.Core.Models;

namespace PuterScanny.Signals.SystemRefs;

/// <summary>
/// Everything on this PC that points at a file or folder: services and drivers, scheduled tasks, startup entries,
/// COM components, file associations, fonts, running programs and the DLLs they have loaded, uninstallers and PATH
/// folders. Each becomes a reason to keep that item. Read-only: nothing is changed.
/// </summary>
public sealed class SystemReferences
{
    private readonly Dictionary<string, List<SystemReference>> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, List<SystemReference>> ByPath => _byPath;

    /// <summary>Sources that could not be read, so gaps are visible in the report.</summary>
    public List<string> Problems { get; } = [];

    public void Add(string path, ReferenceKind kind, Protection level, string detail)
    {
        string key = path.TrimEnd('\\');
        if (key.Length < 3) return;
        if (!_byPath.TryGetValue(key, out var list)) _byPath[key] = list = [];
        if (list.Any(r => r.Kind == kind && r.Detail == detail)) return;
        if (list.Count < 20) list.Add(new SystemReference(kind, level, detail)); // cap: a DLL loaded by 300 processes needs a few examples, not 300
    }

    public bool TryGet(string path, out List<SystemReference> refs) => _byPath.TryGetValue(path, out refs!);
}

public static class SystemReferenceCollector
{
    public static SystemReferences Collect(string windir, IEnumerable<InstalledApp> apps, IProgress<string>? status, CancellationToken ct)
    {
        var refs = new SystemReferences();

        void Step(string label, Action<SystemReferences> collect)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report(label);
            try { collect(refs); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                refs.Problems.Add($"{label.TrimEnd('…')}: {ex.Message}");
            }
        }

        Step("Reading services and drivers…", r => Services(r, windir));
        Step("Reading scheduled tasks…", r => ScheduledTasks(r, windir));
        Step("Reading startup programs…", r => Startup(r, windir));
        Step("Reading registered components…", r => ComComponents(r, windir));
        Step("Reading file associations…", r => FileAssociations(r, windir));
        Step("Reading installed fonts…", r => Fonts(r, windir));
        Step("Reading running programs…", r => RunningPrograms(r, ct));
        Step("Reading uninstallers…", r => Uninstallers(r, apps, windir));
        Step("Reading the PATH…", PathFolders);
        return refs;
    }

    // ------------------------------------------------------------------ services & drivers

    private static void Services(SystemReferences refs, string windir)
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return;
        foreach (string name in services.GetSubKeyNames())
        {
            try { AddService(refs, services, name, windir); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A few services lock their registry key; the rest must still be read.
            }
        }
    }

    private static void AddService(SystemReferences refs, RegistryKey services, string name, string windir)
    {
        {
            using var key = services.OpenSubKey(name);
            if (key is null) return;
            int type = key.GetValue("Type") is int t ? t : 0;
            int start = key.GetValue("Start") is int s ? s : 3;
            string display = DisplayName(key.GetValue("DisplayName") as string) ?? name;
            bool isDriver = (type & 0x3) != 0; // kernel or file-system driver
            var kind = isDriver ? ReferenceKind.Driver : ReferenceKind.Service;
            Protection level = start == 4 ? Protection.Caution : Protection.Required;
            string when = start switch
            {
                0 => "loaded while the PC boots",
                1 => "loaded during Windows start-up",
                2 => "starts automatically",
                3 => "starts when needed",
                4 => "currently disabled",
                _ => "",
            };
            string detail = $"{(isDriver ? "Driver" : "Service")} \"{display}\" ({when})";

            if (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string image)
            {
                foreach (string path in CommandPaths.Extract(image, windir))
                    refs.Add(path, kind, level, detail);
            }
            else if (isDriver)
            {
                // Drivers without ImagePath load from System32\drivers\<name>.sys.
                string implied = Path.Combine(windir, "System32", "drivers", name + ".sys");
                if (File.Exists(implied)) refs.Add(implied, kind, level, detail);
            }

            using var parameters = key.OpenSubKey("Parameters");
            if (parameters?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string dll)
            {
                foreach (string path in CommandPaths.Extract(dll, windir))
                    refs.Add(path, kind, level, detail);
            }
        }
    }

    /// <summary>Resolves "@%SystemRoot%\system32\x.dll,-101" style names; falls back to the raw text.</summary>
    private static string? DisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.StartsWith('@')) return raw;
        var buffer = new char[512];
        return Native.Shlwapi.SHLoadIndirectString(raw, buffer, (uint)buffer.Length, IntPtr.Zero) == 0
            ? new string(buffer).TrimEnd('\0')
            : null;
    }

    // ------------------------------------------------------------------ scheduled tasks

    private static void ScheduledTasks(SystemReferences refs, string windir)
    {
        Type? type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return;
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            WalkFolder(service.GetFolder(@"\"), refs, windir, 0);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    private static void WalkFolder(dynamic folder, SystemReferences refs, string windir, int depth)
    {
        if (depth > 20) return;
        dynamic tasks = folder.GetTasks(1); // include hidden tasks
        foreach (dynamic task in tasks)
        {
            try
            {
                string name = task.Path;
                bool enabled = task.Enabled;
                dynamic actions = task.Definition.Actions;
                foreach (dynamic action in actions)
                {
                    if ((int)action.Type != 0) continue; // 0 = run a program
                    string command = $"\"{(string)action.Path}\" {(string)(action.Arguments ?? "")}";
                    foreach (string path in CommandPaths.Extract(command, windir))
                        refs.Add(path, ReferenceKind.ScheduledTask, enabled ? Protection.Required : Protection.Caution,
                            $"Scheduled task \"{name.TrimStart('\\')}\"{(enabled ? "" : " (disabled)")} runs this");
                }
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
            {
                // A task we may not read (belongs to another user or the system): skip it.
            }
        }

        dynamic subfolders = folder.GetFolders(0);
        foreach (dynamic sub in subfolders)
        {
            try { WalkFolder(sub, refs, windir, depth + 1); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ startup

    private static void Startup(SystemReferences refs, string windir)
    {
        var disabled = DisabledStartupNames();

        foreach (var (hive, view, sub) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(sub);
            if (key is null) continue;
            foreach (string valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string command) continue;
                bool off = disabled.Contains(valueName);
                foreach (string path in CommandPaths.Extract(command, windir))
                    refs.Add(path, ReferenceKind.Startup, off ? Protection.Caution : Protection.Required,
                        $"Starts with Windows (\"{valueName}\"){(off ? ", but turned off in Task Manager" : "")}");
            }
        }

        // Winlogon shell and user initialisation: removing these breaks signing in.
        using (var winlogon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
        {
            foreach (string v in new[] { "Shell", "Userinit" })
                if (winlogon?.GetValue(v) is string cmd)
                    foreach (string part in cmd.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        foreach (string path in CommandPaths.Extract(part, windir))
                            refs.Add(path, ReferenceKind.Startup, Protection.Required, $"Windows sign-in ({v})");
        }

        // Shortcuts in the Startup folders.
        foreach (string folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string shownName = Path.GetFileNameWithoutExtension(file);
                bool off = disabled.Contains(Path.GetFileName(file));
                refs.Add(file, ReferenceKind.Startup, off ? Protection.Caution : Protection.Required, $"Startup shortcut \"{shownName}\"");
                if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShortcutTarget(file) is { } target)
                    refs.Add(target, ReferenceKind.Startup, off ? Protection.Caution : Protection.Required,
                        $"Starts with Windows (shortcut \"{shownName}\"){(off ? ", but turned off in Task Manager" : "")}");
            }
        }
    }

    /// <summary>Entries switched off in Task Manager's Startup tab (first byte of the value is odd when disabled).</summary>
    private static HashSet<string> DisabledStartupNames()
    {
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string sub in new[] { "Run", "Run32", "StartupFolder" })
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + sub);
                if (key is null) continue;
                foreach (string name in key.GetValueNames())
                    if (key.GetValue(name) is byte[] { Length: > 0 } data && (data[0] & 1) == 1) off.Add(name);
            }
        }
        return off;
    }

    private static string? ShortcutTarget(string lnk)
    {
        Type? type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return null;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            Marshal.FinalReleaseComObject(shortcut);
            return string.IsNullOrWhiteSpace(target) || !File.Exists(target) ? null : target;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    // ------------------------------------------------------------------ COM, file associations, fonts

    private static void ComComponents(SystemReferences refs, string windir)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var clsid = root.OpenSubKey("CLSID");
            if (clsid is null) continue;
            foreach (string id in clsid.GetSubKeyNames())
            {
                try { AddComServer(refs, clsid, id, windir); }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
    }

    private static void AddComServer(SystemReferences refs, RegistryKey clsid, string id, string windir)
    {
        {
            {
                using var key = clsid.OpenSubKey(id);
                if (key is null) return;
                string name = key.GetValue(null) as string is { Length: > 0 } n ? n : id;
                foreach (string server in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var s = key.OpenSubKey(server);
                    if (s?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string cmd) continue;
                    foreach (string path in CommandPaths.Extract(cmd, windir))
                        refs.Add(path, ReferenceKind.ComComponent, Protection.Required, $"Registered component \"{Trim(name)}\" that other programs load");
                }
            }
        }
    }

    private static void FileAssociations(SystemReferences refs, string windir)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
        foreach (string progId in root.GetSubKeyNames())
        {
            if (progId.StartsWith('.')) continue; // extensions point to ProgIDs; the commands live on the ProgIDs
            AddVerbs(root, progId, progId, refs, windir);
        }
        using var applications = root.OpenSubKey("Applications");
        if (applications is not null)
            foreach (string app in applications.GetSubKeyNames())
                AddVerbs(applications, app, app, refs, windir);
    }

    private static void AddVerbs(RegistryKey parent, string keyName, string label, SystemReferences refs, string windir)
    {
        try
        {
            using var shell = parent.OpenSubKey(keyName + @"\shell");
            if (shell is null) return;
            foreach (string verb in shell.GetSubKeyNames())
            {
                using var command = shell.OpenSubKey(verb + @"\command");
                if (command?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string cmd) continue;
                foreach (string path in CommandPaths.Extract(cmd.Replace("%1", "").Replace("%L", ""), windir))
                    refs.Add(path, ReferenceKind.FileAssociation, Protection.Required, $"Windows uses it to {VerbText(verb)} files of type \"{Trim(label)}\"");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static string VerbText(string verb) => verb.ToLowerInvariant() switch
    {
        "open" => "open",
        "edit" => "edit",
        "print" => "print",
        "play" => "play",
        "runas" => "run",
        _ => verb.ToLowerInvariant(),
    };

    private static void Fonts(SystemReferences refs, string windir)
    {
        string fontsDir = Path.Combine(windir, "Fonts");
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) continue;
            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string file || file.Length == 0) continue;
                string path = Path.IsPathFullyQualified(file) ? file : Path.Combine(fontsDir, file);
                if (File.Exists(path)) refs.Add(path, ReferenceKind.Font, Protection.Required, $"Installed font \"{name}\"");
            }
        }
    }

    // ------------------------------------------------------------------ running programs

    private static void RunningPrograms(SystemReferences refs, CancellationToken ct)
    {
        foreach (var process in Process.GetProcesses())
        {
            ct.ThrowIfCancellationRequested();
            using (process)
            {
                if (process.Id is 0 or 4) continue; // Idle and System
                string name = process.ProcessName;
                string? image = Native.ProcessImage.QueryPath(process.Id);
                if (image is not null)
                    refs.Add(image, ReferenceKind.RunningProgram, Protection.InUse, $"Was running during the scan ({name})");

                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        string? file = module.FileName;
                        if (file is null || string.Equals(file, image, StringComparison.OrdinalIgnoreCase)) continue;
                        refs.Add(file, ReferenceKind.LoadedByProgram, Protection.InUse, $"Was loaded by {name} during the scan");
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Processes of other users or the system cannot be inspected without administrator rights.
                }
            }
        }
    }

    // ------------------------------------------------------------------ uninstallers & PATH

    private static void Uninstallers(SystemReferences refs, IEnumerable<InstalledApp> apps, string windir)
    {
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.UninstallCommand) || app.UninstallCommand.Contains("://", StringComparison.Ordinal)) continue;
            foreach (string path in CommandPaths.Extract(app.UninstallCommand, windir))
            {
                if (path.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue; // msiexec, rundll32…: Windows' own tools
                refs.Add(path, ReferenceKind.Uninstaller, Protection.UninstallOnly, $"Needed to uninstall {app.Name}");
            }
        }
    }

    private static void PathFolders(SystemReferences refs)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            string? path = Environment.GetEnvironmentVariable("Path", target);
            if (path is null) continue;
            foreach (string raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string dir = Environment.ExpandEnvironmentVariables(raw).TrimEnd('\\');
                if (Directory.Exists(dir))
                    refs.Add(dir, ReferenceKind.PathFolder, Protection.Caution,
                        $"On the {(target == EnvironmentVariableTarget.Machine ? "system" : "user")} PATH: programs and scripts look for tools in this folder");
            }
        }
    }

    private static string Trim(string s) => s.Length > 80 ? s[..77] + "…" : s;
}
