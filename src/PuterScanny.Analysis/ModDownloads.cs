using System.Text.RegularExpressions;

namespace PuterScanny.Analysis;

/// <summary>A game mod downloaded from Nexus Mods, recognised by how Nexus names its downloads.</summary>
public sealed record ModDownload(string Name, int ModId, string Version, DateTime UploadedUtc);

public static partial class ModDownloads
{
    // "Asylum 4K - REMASTERED (Part 1)-36-2-0-1669218064"  =  name - mod id - version (dots as dashes) - upload time (Unix seconds)
    [GeneratedRegex(@"^(?<name>.+?)-(?<mod>\d{1,7})-(?<ver>\d+(?:-\d+){0,4}[a-z]{0,8})-(?<ts>1\d{9})(?<ext>\.(?:zip|7z|rar))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NexusName();

    private static readonly DateTime First = new(2008, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Recognises a downloaded mod archive or the folder it was extracted to.</summary>
    public static bool TryParse(string name, bool isFolder, out ModDownload mod)
    {
        mod = null!;
        var m = NexusName().Match(name);
        if (!m.Success) return false;
        if (!isFolder && !m.Groups["ext"].Success) return false; // a file must be an archive
        if (isFolder && m.Groups["ext"].Success) return false;
        if (!long.TryParse(m.Groups["ts"].Value, out long ts) || !int.TryParse(m.Groups["mod"].Value, out int id) || id <= 0) return false;
        var uploaded = DateTime.UnixEpoch.AddSeconds(ts);
        if (uploaded < First || uploaded > DateTime.UtcNow.AddDays(2)) return false;
        mod = new ModDownload(m.Groups["name"].Value.Trim(), id, m.Groups["ver"].Value.Replace('-', '.'), uploaded);
        return true;
    }
}
