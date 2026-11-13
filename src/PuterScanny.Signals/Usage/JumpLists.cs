using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PuterScanny.Signals.Usage;

/// <summary>One file or folder an app remembers in its jump list.</summary>
public sealed record JumpListItem(string Path, DateTime? LastAccessUtc, int? AccessCount, bool Pinned, string AppId);

/// <summary>
/// Reads "automatic destinations" jump lists (%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations): each is a
/// compound file holding one shortcut per remembered item plus a "DestList" stream with when it was last opened and
/// how often. Items whose DestList entry can't be read safely still count, just without a date/count.
/// </summary>
public static class JumpLists
{
    // Jump list file names are fixed per application. Only well-documented ones are named; others say "an app".
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["f01b4d95cf55d32a"] = "File Explorer",
        ["5f7b5f1e01b83767"] = "File Explorer (Quick access)",
        ["9b9cdc69c1c24e2b"] = "Notepad",
        ["12dc1ea8e34b5a6"] = "Paint",
        ["a7bd71699cd38d1c"] = "Microsoft Word",
        ["d00655d2aa12ff6d"] = "Microsoft PowerPoint",
        ["b8ab77100df80ab2"] = "Microsoft Excel",
        ["9839aec31243a928"] = "Microsoft Excel",
        ["5d696d521de238c3"] = "Google Chrome",
        ["9fda41b86ddcf1db"] = "VLC media player",
    };

    public static string AppName(string appId) => KnownApps.TryGetValue(appId, out var n) ? n : "an app";

    public static List<JumpListItem> Read(string file)
    {
        var items = new List<JumpListItem>();
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return items; }

        string appId = Path.GetFileName(file).Split('.')[0];
        var cf = CompoundFile.TryOpen(data);
        if (cf is null) return items;

        var dest = cf.ReadStream("DestList") is { } bytes ? ParseDestList(bytes) : [];
        var byEntry = dest.ToDictionary(d => d.EntryNumber);

        foreach (string name in cf.StreamNames)
        {
            if (!int.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int entry)) continue;
            string? target = cf.ReadStream(name) is { } lnk ? ShellLink.ParseTarget(lnk) : null;
            if (byEntry.TryGetValue(entry, out var d))
            {
                target ??= d.Path;
                if (target is not null) items.Add(new JumpListItem(target, d.LastAccess, d.Count, d.Pinned, appId));
            }
            else if (target is not null)
            {
                items.Add(new JumpListItem(target, null, null, false, appId));
            }
        }
        return items;
    }

    internal sealed record DestEntry(int EntryNumber, string Path, DateTime? LastAccess, int? Count, bool Pinned);

    /// <summary>DestList version 3+ (Windows 10/11). Stops at the first entry that does not look right.</summary>
    internal static List<DestEntry> ParseDestList(byte[] d)
    {
        var list = new List<DestEntry>();
        if (d.Length < 32) return list;
        int version = BinaryPrimitives.ReadInt32LittleEndian(d);
        int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        if (version < 3 || count is < 0 or > 10_000) return list;

        int pos = 32;
        for (int i = 0; i < count && pos + 130 <= d.Length; i++)
        {
            var e = d.AsSpan(pos);
            int entryNumber = BinaryPrimitives.ReadInt32LittleEndian(e[88..]);
            long fileTime = BinaryPrimitives.ReadInt64LittleEndian(e[100..]);
            int pin = BinaryPrimitives.ReadInt32LittleEndian(e[108..]);
            int accessCount = BinaryPrimitives.ReadInt32LittleEndian(e[116..]);
            int pathChars = BinaryPrimitives.ReadUInt16LittleEndian(e[128..]);
            if (pathChars is 0 or > 2048 || pos + 130 + pathChars * 2 > d.Length) break;
            string path = Encoding.Unicode.GetString(d, pos + 130, pathChars * 2);

            DateTime? when = null;
            if (fileTime > 0)
            {
                try
                {
                    var t = DateTime.FromFileTimeUtc(fileTime);
                    if (t.Year >= 2000 && t <= DateTime.UtcNow.AddDays(1)) when = t;
                }
                catch (ArgumentOutOfRangeException) { }
            }
            if (when is null && fileTime != 0) break; // misaligned: stop rather than invent dates
            if (path.Contains("://", StringComparison.Ordinal)) path = ""; // web/cloud items (Outlook, SharePoint) are not files on this PC

            list.Add(new DestEntry(entryNumber, path, when, accessCount is > 0 and < 1_000_000 ? accessCount : null, pin != -1));

            // After the path comes the size of an optional extra block (Office stores cloud-file properties there).
            int extraAt = pos + 130 + pathChars * 2;
            if (extraAt + 4 > d.Length) break;
            int extra = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(extraAt));
            if (extra < 0 || extraAt + 4 + extra > d.Length) break;
            pos = extraAt + 4 + extra;
        }
        return list;
    }
}
