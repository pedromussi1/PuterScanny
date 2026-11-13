using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PuterScanny.Core.Models;

namespace PuterScanny.Signals.Content;

/// <summary>
/// Reads a Windows program file's headers and version resource straight from an open handle (no loading,
/// no executing): architecture, program type, .NET or native, and the publisher/product/version text that
/// File Explorer shows on the Details tab.
/// </summary>
public static class PeReader
{
    private const int RT_VERSION = 16;
    private const int MaxVersionResource = 1024 * 1024;

    public sealed record Result(PeInfo Info, bool IsValidPe);

    public static Result? Read(SafeFileHandle handle, long fileSize)
    {
        if (fileSize < 64) return null;
        Span<byte> dos = stackalloc byte[64];
        if (SafeFileReader.ReadAt(handle, 0, dos) < 64 || dos[0] != 'M' || dos[1] != 'Z') return null;

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (peOffset <= 0 || peOffset > fileSize - 24 || peOffset > 16 * 1024 * 1024)
            return new Result(new PeInfo { Subsystem = "MS-DOS" }, false);

        byte[] nt = new byte[(int)Math.Min(4096, fileSize - peOffset)];
        int ntLen = SafeFileReader.ReadAt(handle, peOffset, nt);
        if (ntLen < 24 || nt[0] != 'P' || nt[1] != 'E' || nt[2] != 0 || nt[3] != 0)
            return new Result(new PeInfo { Subsystem = "MS-DOS" }, false);

        ushort machine = U16(nt, 4);
        int sectionCount = U16(nt, 6);
        uint timeStamp = U32(nt, 8);
        int optSize = U16(nt, 20);
        ushort characteristics = U16(nt, 22);
        int opt = 24;
        if (opt + 2 > ntLen) return null;

        ushort magic = U16(nt, opt);
        bool is64 = magic == 0x20B;
        if (magic is not (0x10B or 0x20B) || opt + 70 > ntLen) return new Result(new PeInfo { Machine = MachineName(machine) }, false);

        ushort subsystem = U16(nt, opt + 68);
        int rvaCountOffset = opt + (is64 ? 108 : 92);
        int dirStart = opt + (is64 ? 112 : 96);
        uint rvaCount = rvaCountOffset + 4 <= ntLen ? U32(nt, rvaCountOffset) : 0;

        (uint Rva, uint Size) Dir(int index) =>
            index < rvaCount && dirStart + index * 8 + 8 <= ntLen
                ? (U32(nt, dirStart + index * 8), U32(nt, dirStart + index * 8 + 4))
                : (0u, 0u);

        // Section table, needed to turn memory addresses (RVAs) into file offsets.
        int secStart = opt + optSize;
        var sections = new List<(uint Va, uint VSize, uint RawPtr, uint RawSize)>();
        if (sectionCount is > 0 and <= 96)
        {
            byte[] secBytes = new byte[sectionCount * 40];
            if (SafeFileReader.ReadAt(handle, peOffset + secStart, secBytes) == secBytes.Length)
            {
                for (int i = 0; i < sectionCount; i++)
                {
                    int s = i * 40;
                    sections.Add((U32(secBytes, s + 12), U32(secBytes, s + 8), U32(secBytes, s + 20), U32(secBytes, s + 16)));
                }
            }
        }

        long RvaToOffset(uint rva)
        {
            foreach (var s in sections)
            {
                uint span = Math.Max(s.VSize, s.RawSize);
                if (rva >= s.Va && rva < s.Va + span) return s.RawPtr + (rva - s.Va);
            }
            return -1;
        }

        var security = Dir(4);   // file offset (not an RVA) of the embedded signature
        var clr = Dir(14);
        var resources = Dir(2);

        bool isDll = (characteristics & 0x2000) != 0;
        bool isDriver = subsystem == 1 && !isDll; // native subsystem: kernel drivers and boot-time programs

        var info = new PeInfo
        {
            Machine = MachineName(machine),
            Subsystem = SubsystemName(subsystem),
            IsDll = isDll,
            IsDriver = isDriver,
            IsDotNet = clr.Size > 0,
            LinkTimeUtc = PlausibleTime(timeStamp),
            HasEmbeddedSignature = security.Size > 0 && security.Rva > 0 && security.Rva < fileSize,
        };

        if (resources.Size > 0)
        {
            long resOffset = RvaToOffset(resources.Rva);
            if (resOffset > 0)
            {
                try
                {
                    var strings = ReadVersionStrings(handle, resOffset, resources.Rva, RvaToOffset, fileSize, out string? fixedFileVersion, out string? fixedProductVersion);
                    info = info with
                    {
                        CompanyName = Get(strings, "CompanyName"),
                        ProductName = Get(strings, "ProductName"),
                        FileDescription = Get(strings, "FileDescription"),
                        FileVersion = Get(strings, "FileVersion") ?? fixedFileVersion,
                        ProductVersion = Get(strings, "ProductVersion") ?? fixedProductVersion,
                        OriginalFilename = Get(strings, "OriginalFilename"),
                        InternalName = Get(strings, "InternalName"),
                        LegalCopyright = Get(strings, "LegalCopyright"),
                    };
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or IOException)
                {
                    // Damaged or unusual resources: keep the header facts.
                }
            }
        }

        return new Result(info, true);
    }

    private static string? Get(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static Dictionary<string, string> ReadVersionStrings(
        SafeFileHandle handle, long resOffset, uint resRva, Func<uint, long> rvaToOffset, long fileSize,
        out string? fixedFileVersion, out string? fixedProductVersion)
    {
        fixedFileVersion = fixedProductVersion = null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Level 1: resource type → RT_VERSION
        long typeDir = FindEntry(handle, resOffset, resOffset, id => id == RT_VERSION);
        if (typeDir < 0) return result;
        // Level 2: resource name/id → first entry
        long nameDir = FindEntry(handle, resOffset, typeDir, _ => true);
        if (nameDir < 0) return result;
        // Level 3: language → prefer US English, else the first
        long dataEntry = FindEntry(handle, resOffset, nameDir, id => id == 0x409, fallbackToFirst: true, wantLeaf: true);
        if (dataEntry < 0) return result;

        Span<byte> de = stackalloc byte[16];
        if (SafeFileReader.ReadAt(handle, dataEntry, de) != 16) return result;
        uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(de);
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(de[4..]);
        if (dataSize is 0 or > MaxVersionResource) return result;
        long dataOffset = rvaToOffset(dataRva);
        if (dataOffset < 0 || dataOffset + dataSize > fileSize) return result;

        byte[] data = new byte[dataSize];
        if (SafeFileReader.ReadAt(handle, dataOffset, data) != data.Length) return result;

        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? firstTable = null;
        string? fileVer = null, productVer = null;
        WalkVersionBlocks(data, 0, data.Length, [], (path, key, valueOffset, valueLength, type) =>
        {
            if (path.Count == 0 && key == "VS_VERSION_INFO" && valueLength >= 52 && valueOffset + 52 <= data.Length
                && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(valueOffset)) == 0xFEEF04BD)
            {
                fileVer = Version(data, valueOffset + 8);
                productVer = Version(data, valueOffset + 16);
            }
            else if (path.Count == 3 && path[1] == "StringFileInfo")
            {
                if (!tables.TryGetValue(path[2], out var table))
                {
                    tables[path[2]] = table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    firstTable ??= path[2];
                }
                int bytes = Math.Min(valueLength * 2, data.Length - valueOffset);
                if (bytes > 0) table[key] = Encoding.Unicode.GetString(data, valueOffset, bytes).TrimEnd('\0');
            }
        });

        fixedFileVersion = fileVer;
        fixedProductVersion = productVer;

        // Prefer the US English table when several languages exist.
        var english = tables.Keys.FirstOrDefault(k => k.StartsWith("0409", StringComparison.OrdinalIgnoreCase));
        string? chosen = english ?? firstTable;
        if (chosen is not null)
            foreach (var kv in tables[chosen]) result[kv.Key] = kv.Value;
        return result;
    }

    private static string Version(byte[] d, int at)
    {
        uint ms = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
        uint ls = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 4));
        return $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
    }

    private delegate void BlockVisitor(List<string> path, string key, int valueOffset, int valueLength, int type);

    /// <summary>Walks the nested VS_VERSIONINFO blocks (length, value length, type, key, value, children; 32-bit aligned).</summary>
    private static void WalkVersionBlocks(byte[] data, int start, int end, List<string> path, BlockVisitor visit)
    {
        int pos = start;
        int guard = 0;
        while (pos + 6 <= end && guard++ < 4096)
        {
            int length = U16(data, pos);
            if (length < 6) break;
            int blockEnd = Math.Min(pos + length, end);
            int valueLength = U16(data, pos + 2);
            int type = U16(data, pos + 4);

            int keyStart = pos + 6;
            int keyEnd = keyStart;
            while (keyEnd + 1 < blockEnd && (data[keyEnd] != 0 || data[keyEnd + 1] != 0)) keyEnd += 2;
            string key = Encoding.Unicode.GetString(data, keyStart, keyEnd - keyStart);

            int valueStart = Align4(keyEnd + 2);
            int valueBytes = type == 1 ? valueLength * 2 : valueLength;
            if (valueStart > blockEnd) valueStart = blockEnd;
            visit(path, key, valueStart, type == 1 ? valueLength : valueBytes, type);

            int childStart = Align4(valueStart + Math.Min(valueBytes, blockEnd - valueStart));
            if (childStart < blockEnd && path.Count < 4)
            {
                path.Add(key);
                WalkVersionBlocks(data, childStart, blockEnd, path, visit);
                path.RemoveAt(path.Count - 1);
            }
            pos = Align4(pos + length);
        }
    }

    /// <summary>Finds a child in a resource directory; returns the absolute file offset of the child directory or data entry.</summary>
    private static long FindEntry(SafeFileHandle handle, long resBase, long dirOffset, Func<int, bool> match, bool fallbackToFirst = false, bool wantLeaf = false)
    {
        Span<byte> header = stackalloc byte[16];
        if (SafeFileReader.ReadAt(handle, dirOffset, header) != 16) return -1;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) + BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        if (count is 0 or > 4096) return -1;

        byte[] entries = new byte[count * 8];
        if (SafeFileReader.ReadAt(handle, dirOffset + 16, entries) != entries.Length) return -1;

        long first = -1;
        for (int i = 0; i < count; i++)
        {
            uint name = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8));
            uint target = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4));
            bool isDir = (target & 0x80000000) != 0;
            if (isDir == wantLeaf) continue; // leaf wanted: skip folders; folder wanted: skip leaves
            long childOffset = resBase + (target & 0x7FFFFFFF);
            if (first < 0) first = childOffset;
            bool isId = (name & 0x80000000) == 0;
            if (isId && match((int)name)) return childOffset;
            if (!isId && match(-1)) return childOffset;
        }
        return fallbackToFirst ? first : -1;
    }

    private static int Align4(int v) => (v + 3) & ~3;
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    private static DateTime? PlausibleTime(uint seconds)
    {
        // Modern Microsoft builds store a hash here instead of a time; only keep believable dates.
        var t = DateTime.UnixEpoch.AddSeconds(seconds);
        return t.Year >= 1995 && t <= DateTime.UtcNow.AddDays(1) ? t : null;
    }

    public static string MachineName(ushort machine) => machine switch
    {
        0x014C => "32-bit (x86)",
        0x8664 => "64-bit (x64)",
        0xAA64 => "ARM64",
        0xA641 => "ARM64EC",
        0x01C4 => "ARM (32-bit)",
        0x0200 => "Itanium",
        0x0EBC => "EFI byte code",
        0 => "Any",
        _ => $"Other (0x{machine:X4})",
    };

    public static string SubsystemName(ushort subsystem) => subsystem switch
    {
        1 => "Native (driver or boot-time program)",
        2 => "Windows app (graphical)",
        3 => "Console app (command line)",
        5 => "OS/2 console",
        7 => "POSIX console",
        9 => "Windows CE",
        10 => "UEFI application",
        11 => "UEFI boot driver",
        12 => "UEFI runtime driver",
        13 => "UEFI ROM",
        14 => "Xbox",
        16 => "Windows boot application",
        _ => "Unknown",
    };
}
