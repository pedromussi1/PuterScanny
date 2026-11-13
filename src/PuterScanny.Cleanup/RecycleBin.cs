using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PuterScanny.Cleanup;

/// <summary>
/// Sends items to Windows' Recycle Bin (the same way File Explorer does) and can put them back. Restoring works by
/// reading the Recycle Bin's own "$I" record of where each item came from.
/// </summary>
public static class RecycleBin
{
    /// <summary>Paths longer than this can't go through the Recycle Bin API; they go to quarantine instead.</summary>
    public const int MaxPath = 259;

    public sealed record Result(bool Ok, string? Error, string? StoredAt, string? InfoFile);

    public static Result Send(string path)
    {
        if (path.Length > MaxPath) return new(false, "The path is too long for the Recycle Bin.", null, null);
        DateTime before = DateTime.UtcNow.AddSeconds(-2);

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            // ALLOWUNDO = Recycle Bin. WANTNUKEWARNING makes Windows ask before it would delete permanently (for
            // example when an item is too big for the Recycle Bin), even though routine confirmations are off.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0) return new(false, $"Windows could not recycle it (error 0x{rc:X}).", null, null);
        if (op.fAnyOperationsAborted) return new(false, "Cancelled.", null, null);
        if (File.Exists(path) || Directory.Exists(path)) return new(false, "It is still there after recycling.", null, null);

        var found = FindInBin(path, before);
        return new(true, null, found?.StoredAt, found?.InfoFile);
    }

    /// <summary>Puts a recycled item back where it was. Never overwrites: if something new is there, it restores next to it.</summary>
    public static (bool Ok, string Message) Restore(string originalPath, string? storedAt, string? infoFile)
    {
        if (storedAt is null || infoFile is null)
        {
            var found = FindInBin(originalPath, DateTime.MinValue);
            if (found is null) return (false, "It's no longer in the Recycle Bin (it may have been emptied or restored already).");
            (storedAt, infoFile) = (found.Value.StoredAt, found.Value.InfoFile);
        }
        if (!File.Exists(storedAt) && !Directory.Exists(storedAt))
            return (false, "It's no longer in the Recycle Bin (it may have been emptied or restored already).");

        string target = FreeTarget(originalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(storedAt)) Directory.Move(storedAt, target);
        else File.Move(storedAt, target);
        try { File.Delete(infoFile); } catch (IOException) { }
        return (true, target == originalPath ? "Restored." : $"Restored as \"{Path.GetFileName(target)}\" because a new item now has the old name.");
    }

    internal static string FreeTarget(string originalPath)
    {
        if (!File.Exists(originalPath) && !Directory.Exists(originalPath)) return originalPath;
        string dir = Path.GetDirectoryName(originalPath)!;
        string name = Path.GetFileNameWithoutExtension(originalPath), ext = Path.GetExtension(originalPath);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} (restored{(i == 1 ? "" : " " + i)}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Finds the newest Recycle Bin entry for a path, deleted after <paramref name="after"/>.</summary>
    public static (string StoredAt, string InfoFile)? FindInBin(string originalPath, DateTime after)
    {
        string? root = Path.GetPathRoot(originalPath);
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (root is null || sid is null) return null;
        string bin = Path.Combine(root, "$Recycle.Bin", sid);
        if (!Directory.Exists(bin)) return null;

        (string, string)? best = null;
        DateTime bestTime = DateTime.MinValue;
        IEnumerable<string> infos;
        try { infos = Directory.EnumerateFiles(bin, "$I*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        foreach (string info in infos)
        {
            if (ReadInfo(info) is not { } rec) continue;
            if (!string.Equals(rec.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase) || rec.DeletedUtc < after) continue;
            string stored = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
            if (!File.Exists(stored) && !Directory.Exists(stored)) continue;
            if (rec.DeletedUtc >= bestTime)
            {
                bestTime = rec.DeletedUtc;
                best = (stored, info);
            }
        }
        return best;
    }

    /// <summary>Reads a $I file: version 1 (Vista–8.1, fixed 520-byte path) or version 2 (Windows 10+, length-prefixed path).</summary>
    public static (string OriginalPath, long Size, DateTime DeletedUtc)? ReadInfo(string infoFile)
    {
        try
        {
            byte[] d = File.ReadAllBytes(infoFile);
            if (d.Length < 28) return null;
            long version = BinaryPrimitives.ReadInt64LittleEndian(d);
            long size = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(8));
            var deleted = DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(16)));
            string path = version switch
            {
                2 => Encoding.Unicode.GetString(d, 28, Math.Min(d.Length - 28, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(24)) * 2)),
                1 => Encoding.Unicode.GetString(d, 24, Math.Min(d.Length - 24, 520)),
                _ => "",
            };
            path = path.TrimEnd('\0');
            int nul = path.IndexOf('\0');
            if (nul >= 0) path = path[..nul];
            return path.Length == 0 ? null : (path, size, deleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
