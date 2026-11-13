using System.Buffers.Binary;
using System.Text;

namespace PuterScanny.Signals.Usage;

/// <summary>
/// Reads the target path of a Windows shortcut (.lnk) straight from its bytes (MS-SHLLINK), without COM and
/// without touching the target.
/// </summary>
public static class ShellLink
{
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;

    public static string? ReadTarget(string lnkPath)
    {
        try
        {
            var info = new FileInfo(lnkPath);
            if (!info.Exists || info.Length is < 0x4C or > 1024 * 1024) return null;
            return ParseTarget(File.ReadAllBytes(lnkPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ParseTarget(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x4C || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x4C) return null;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        int pos = 0x4C;

        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (pos + 2 > data.Length) return null;
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        }
        if ((flags & HasLinkInfo) == 0 || pos + 28 > data.Length) return null;

        var li = data[pos..];
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(li);
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[4..]);
        uint liFlags = BinaryPrimitives.ReadUInt32LittleEndian(li[8..]);
        if (size < 28 || size > li.Length) return null;
        li = li[..size];

        int localBase = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[16..]);
        int networkLink = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[20..]);
        int suffix = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[24..]);
        bool unicode = headerSize >= 0x24 && li.Length >= 0x24;
        int localBaseW = unicode ? (int)BinaryPrimitives.ReadUInt32LittleEndian(li[28..]) : 0;
        int suffixW = unicode ? (int)BinaryPrimitives.ReadUInt32LittleEndian(li[32..]) : 0;

        string tail = unicode && suffixW > 0 ? Utf16Z(li, suffixW) : AnsiZ(li, suffix);

        if ((liFlags & 0x1) != 0) // local volume + base path
        {
            string basePath = unicode && localBaseW > 0 ? Utf16Z(li, localBaseW) : AnsiZ(li, localBase);
            return Join(basePath, tail);
        }
        if ((liFlags & 0x2) != 0 && networkLink > 0 && networkLink + 20 <= li.Length) // \\server\share
        {
            var cnrl = li[networkLink..];
            int netNameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(cnrl[8..]);
            string share = AnsiZ(cnrl, netNameOffset);
            return Join(share, tail);
        }
        return null;
    }

    private static string? Join(string root, string tail)
    {
        if (root.Length == 0) return null;
        if (tail.Length == 0) return root;
        return root.EndsWith('\\') ? root + tail : root + "\\" + tail;
    }

    private static string AnsiZ(ReadOnlySpan<byte> s, int offset)
    {
        if (offset <= 0 || offset >= s.Length) return "";
        var rest = s[offset..];
        int end = rest.IndexOf((byte)0);
        if (end < 0) end = rest.Length;
        return Encoding.Latin1.GetString(rest[..end]);
    }

    private static string Utf16Z(ReadOnlySpan<byte> s, int offset)
    {
        if (offset <= 0 || offset >= s.Length) return "";
        var rest = s[offset..];
        int end = 0;
        while (end + 1 < rest.Length && (rest[end] != 0 || rest[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(rest[..end]);
    }
}
