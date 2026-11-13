using System.Text.RegularExpressions;

namespace PuterScanny.Knowledge;

/// <summary>Expands <c>{token}</c> placeholders in knowledge-base paths to this PC's real folders.</summary>
public sealed partial class PathTokens
{
    private readonly Dictionary<string, string> _values;

    public PathTokens(IDictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Token values for the current machine.</summary>
    public static PathTokens ForThisMachine()
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windir)) windir = @"C:\Windows";
        string systemDrive = Path.GetPathRoot(windir)!.TrimEnd('\\');
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string users = Path.GetDirectoryName(profile) ?? systemDrive + @"\Users";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["windir"] = windir,
            ["systemdrive"] = systemDrive,
            ["programfiles"] = NonEmpty(Environment.GetEnvironmentVariable("ProgramW6432"))
                               ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["programfilesx86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["programdata"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["users"] = users,
            ["anydrive"] = "?:",
        };
        foreach (var key in map.Keys.ToList())
        {
            if (string.IsNullOrEmpty(map[key])) map.Remove(key);
            else map[key] = map[key].TrimEnd('\\');
        }
        return new PathTokens(map);
    }

    /// <summary>Returns the expanded path, or null when a token is unknown on this machine.</summary>
    public string? Expand(string pattern)
    {
        bool missing = false;
        string result = TokenRegex().Replace(pattern, m =>
        {
            if (_values.TryGetValue(m.Groups[1].Value, out var v)) return v;
            missing = true;
            return m.Value;
        });
        return missing ? null : result;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex(@"\{([a-z0-9]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
}
