namespace PuterScanny.Signals.SystemRefs;

/// <summary>
/// Pulls real file paths out of the command lines Windows stores for services, drivers, tasks, startup entries
/// and file associations, e.g. <c>"C:\Program Files\App\app.exe" -k</c>, <c>\SystemRoot\System32\drivers\x.sys</c>,
/// <c>rundll32.exe C:\x\y.dll,Entry</c> or <c>%SystemRoot%\system32\svchost.exe -k netsvcs</c>.
/// </summary>
public static class CommandPaths
{
    private static readonly string[] ExecutableExtensions = [".exe", ".sys", ".dll", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".scr", ".cpl", ".msc", ".ocx", ".jar", ".py", ".pyw"];

    /// <summary>Every existing file mentioned in the command: the program itself plus any file paths in its arguments.</summary>
    public static List<string> Extract(string? command, string windir, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(command)) return result;

        string cmd = Normalize(Environment.ExpandEnvironmentVariables(command.Trim()), windir);

        // 1. The program being run.
        string rest;
        string? program;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            program = end > 1 ? cmd[1..end] : cmd.Trim('"');
            rest = end > 1 ? cmd[(end + 1)..] : "";
        }
        else
        {
            (program, rest) = SplitUnquoted(cmd, exists);
        }

        if (program is not null)
        {
            string? resolved = Resolve(Normalize(program, windir), windir, exists);
            if (resolved is not null) result.Add(resolved);
        }

        // 2. File paths in the arguments (rundll32 DLLs, scripts run by wscript/powershell, …).
        foreach (string token in Tokens(rest))
        {
            string t = token.Trim().TrimStart('/', '-');
            int comma = t.IndexOf(',');               // rundll32 "x.dll,EntryPoint"
            if (comma > 0) t = t[..comma];
            int eq = t.IndexOf('=');                  // --config=C:\x.json
            if (eq > 0 && eq < t.Length - 3 && t[eq + 2] == ':') t = t[(eq + 1)..];
            t = t.Trim('"');
            if (t.Length < 4 || t[1] != ':' || t[2] != '\\') continue;
            if (exists(t) && !result.Contains(t, StringComparer.OrdinalIgnoreCase)) result.Add(t);
        }
        return result;
    }

    /// <summary>Windows-internal path forms → ordinary paths.</summary>
    public static string Normalize(string path, string windir)
    {
        string p = path.Trim();
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = windir + p[11..];
        else if (p.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = windir + p[10..];
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(windir, p);
        return p;
    }

    /// <summary>Unquoted commands with spaces ("C:\Program Files\App\app.exe /x"): the longest prefix that is a real program.</summary>
    private static (string? Program, string Remainder) SplitUnquoted(string cmd, Func<string, bool> exists)
    {
        foreach (string ext in ExecutableExtensions)
        {
            int at = cmd.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            while (at > 0)
            {
                int end = at + ext.Length;
                if (end == cmd.Length || cmd[end] is ' ' or ',' or '"')
                {
                    string candidate = cmd[..end];
                    if (!Path.IsPathFullyQualified(candidate) || exists(candidate)) return (candidate, cmd[end..]);
                }
                at = cmd.IndexOf(ext, end, StringComparison.OrdinalIgnoreCase);
            }
        }
        int space = cmd.IndexOf(' ');
        return space > 0 ? (cmd[..space], cmd[space..]) : (cmd, "");
    }

    /// <summary>Bare names ("svchost.exe", "rundll32") are looked up in System32, like Windows does.</summary>
    private static string? Resolve(string program, string windir, Func<string, bool> exists)
    {
        if (Path.IsPathFullyQualified(program))
        {
            if (exists(program)) return program;
            if (!Path.HasExtension(program) && exists(program + ".exe")) return program + ".exe";
            return null;
        }
        if (program.Contains('\\') || program.Length == 0) return null;
        foreach (string dir in new[] { Path.Combine(windir, "System32"), windir })
        {
            string candidate = Path.Combine(dir, program);
            if (exists(candidate)) return candidate;
            if (!Path.HasExtension(candidate) && exists(candidate + ".exe")) return candidate + ".exe";
        }
        return null;
    }

    private static IEnumerable<string> Tokens(string args)
    {
        int i = 0;
        while (i < args.Length)
        {
            while (i < args.Length && args[i] == ' ') i++;
            if (i >= args.Length) yield break;
            int start = i;
            if (args[i] == '"')
            {
                int end = args.IndexOf('"', i + 1);
                if (end < 0) end = args.Length - 1;
                yield return args[(start + 1)..end];
                i = end + 1;
            }
            else
            {
                while (i < args.Length && args[i] != ' ') i++;
                yield return args[start..i];
            }
        }
    }
}

/// <summary>Windows Resource Protection: files Windows guards and restores itself.</summary>
public static class WindowsResourceProtection
{
    public static bool IsProtected(string path) => Native.Sfc.IsProtected(path);
}
