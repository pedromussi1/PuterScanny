namespace PuterScanny.Core.IO;

/// <summary>Converts between normal paths and the <c>\\?\</c> form Windows needs for paths over 260 characters.</summary>
public static class LongPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    public static string ToNative(string path)
    {
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return UncPrefix + path[2..];
        return Prefix + path;
    }

    public static string ToDisplay(string path)
    {
        if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path[UncPrefix.Length..];
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path[Prefix.Length..];
        return path;
    }

    /// <summary>Joins a folder and child name without doubling the separator (drive roots already end in '\').</summary>
    public static string Combine(string directory, string name) =>
        directory.EndsWith('\\') ? directory + name : directory + "\\" + name;

    /// <summary>Normalizes a user-chosen root: full path, trailing separator kept only for drive roots.</summary>
    public static string NormalizeRoot(string path)
    {
        string full = System.IO.Path.GetFullPath(ToDisplay(path));
        string? root = System.IO.Path.GetPathRoot(full);
        if (root is not null && string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return root.EndsWith('\\') ? root : root + "\\";
        return full.TrimEnd('\\');
    }
}
