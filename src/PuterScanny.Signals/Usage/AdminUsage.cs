using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PuterScanny.Signals.Usage;

/// <summary>
/// Windows' Prefetch records (C:\Windows\Prefetch\*.pf): for every program that ran, how many times and the last
/// eight run times. Readable only with administrator rights. Windows 10/11 compresses them (XPRESS Huffman);
/// they're decompressed with Windows' own routine.
/// </summary>
public static class PrefetchReader
{
    public sealed record Entry(string ExeName, string? FullPath, int RunCount, IReadOnlyList<DateTime> RunTimesUtc);

    public static Entry? ReadFile(string pfPath, IReadOnlyDictionary<string, string> volumeSerialToDrive)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(pfPath);
            byte[]? data = Decompress(raw);
            return data is null ? null : Parse(data, volumeSerialToDrive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"MAM\x04" + uncompressed size + XPRESS-Huffman data (Windows 10+); older files are stored as-is.</summary>
    public static byte[]? Decompress(byte[] raw)
    {
        if (raw.Length >= 8 && raw[0] == 'M' && raw[1] == 'A' && raw[2] == 'M' && raw[3] == 4)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
            if (size is <= 0 or > 64 * 1024 * 1024) return null;
            if (Rtl.RtlGetCompressionWorkSpaceSize(Rtl.COMPRESSION_FORMAT_XPRESS_HUFF, out uint ws, out _) != 0) return null;
            var output = new byte[size];
            var workspace = new byte[ws];
            byte[] compressed = raw[8..];
            int status = Rtl.RtlDecompressBufferEx(Rtl.COMPRESSION_FORMAT_XPRESS_HUFF, output, (uint)output.Length, compressed, (uint)compressed.Length, out uint final, workspace);
            return status == 0 && final > 0 ? output[..(int)final] : null;
        }
        return raw.Length >= 8 && raw[4] == 'S' && raw[5] == 'C' && raw[6] == 'C' && raw[7] == 'A' ? raw : null;
    }

    /// <summary>Parses an uncompressed "SCCA" prefetch file (versions 23, 26, 30 and 31).</summary>
    public static Entry? Parse(byte[] d, IReadOnlyDictionary<string, string> volumeSerialToDrive)
    {
        if (d.Length < 0xD4 || d[4] != 'S' || d[5] != 'C' || d[6] != 'C' || d[7] != 'A') return null;
        int version = BinaryPrimitives.ReadInt32LittleEndian(d);
        string exe = Encoding.Unicode.GetString(d, 0x10, 60).Split('\0')[0];

        var times = new List<DateTime>();
        int runCount;
        switch (version)
        {
            case 23: // Windows 7
                AddTime(times, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(0x80)));
                runCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x98));
                break;
            case 26: case 30: case 31: // Windows 8, 10, 11
                for (int i = 0; i < 8; i++) AddTime(times, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(0x80 + i * 8)));
                int metricsOffset = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x54));
                runCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(version >= 30 && metricsOffset == 0x128 ? 0xC8 : 0xD0));
                break;
            default:
                return null;
        }
        if (runCount is < 0 or > 10_000_000) runCount = 0;

        // The full path is among the file names the program loaded, in the form \VOLUME{created-SERIAL}\PATH.
        string? fullPath = null;
        int namesOffset = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x64));
        int namesSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x68));
        if (namesOffset > 0 && namesSize > 0 && namesOffset + namesSize <= d.Length)
        {
            foreach (string name in Encoding.Unicode.GetString(d, namesOffset, namesSize).Split('\0'))
            {
                if (!name.EndsWith("\\" + exe, StringComparison.OrdinalIgnoreCase)) continue;
                fullPath = ToDrivePath(name, volumeSerialToDrive);
                if (fullPath is not null) break;
            }
        }
        times.Sort((a, b) => b.CompareTo(a));
        return new Entry(exe, fullPath, runCount, times);
    }

    private static void AddTime(List<DateTime> times, long ft)
    {
        if (ft <= 0) return;
        try
        {
            var t = DateTime.FromFileTimeUtc(ft);
            if (t.Year >= 2000 && t <= DateTime.UtcNow.AddDays(1)) times.Add(t);
        }
        catch (ArgumentOutOfRangeException) { }
    }

    internal static string? ToDrivePath(string volumePath, IReadOnlyDictionary<string, string> serialToDrive)
    {
        // \VOLUME{01d7a9c4f2e3b8a0-1a2b3c4d}\WINDOWS\SYSTEM32\CMD.EXE
        if (!volumePath.StartsWith(@"\VOLUME{", StringComparison.OrdinalIgnoreCase)) return null;
        int close = volumePath.IndexOf('}');
        int dash = volumePath.LastIndexOf('-', close);
        if (close < 0 || dash < 0) return null;
        string serial = volumePath[(dash + 1)..close].ToUpperInvariant();
        return serialToDrive.TryGetValue(serial, out var drive) ? drive + volumePath[(close + 1)..] : null;
    }

    /// <summary>Volume serial number (8 hex digits) → "C:" for every ready drive.</summary>
    public static Dictionary<string, string> VolumeSerials()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (Rtl.GetVolumeInformation(d.Name, null, 0, out uint serial, out _, out _, null, 0))
                    map[serial.ToString("X8")] = d.Name.TrimEnd('\\');
            }
            catch (IOException) { }
        }
        return map;
    }

    private static class Rtl
    {
        public const ushort COMPRESSION_FORMAT_XPRESS_HUFF = 4;

        [DllImport("ntdll.dll")]
        public static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint workSpaceSize, out uint fragmentWorkSpaceSize);

        [DllImport("ntdll.dll")]
        public static extern int RtlDecompressBufferEx(ushort format, byte[] uncompressed, uint uncompressedSize, byte[] compressed, uint compressedSize, out uint finalSize, byte[] workspace);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder? fsName, int fsNameSize);
    }

    /// <summary>For tests: compress with Windows' XPRESS-Huffman so the decompression path can be checked without administrator rights.</summary>
    public static byte[] CompressForTest(byte[] data)
    {
        RtlCompress.RtlGetCompressionWorkSpaceSize(4, out uint ws, out _);
        var workspace = new byte[ws];
        var output = new byte[data.Length + 4096];
        RtlCompress.RtlCompressBuffer(4, data, (uint)data.Length, output, (uint)output.Length, 4096, out uint size, workspace);
        var result = new byte[8 + size];
        result[0] = (byte)'M'; result[1] = (byte)'A'; result[2] = (byte)'M'; result[3] = 4;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), data.Length);
        Array.Copy(output, 0, result, 8, size);
        return result;
    }

    private static class RtlCompress
    {
        [DllImport("ntdll.dll")]
        public static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint workSpaceSize, out uint fragmentWorkSpaceSize);

        [DllImport("ntdll.dll")]
        public static extern int RtlCompressBuffer(ushort format, byte[] uncompressed, uint uncompressedSize, byte[] compressed, uint compressedSize, uint chunkSize, out uint finalSize, byte[] workspace);
    }
}

/// <summary>
/// Windows' Background Activity Moderator record: when each program last ran, per user. Readable only with
/// administrator rights. Paths are stored as \Device\HarddiskVolumeN\…, converted here to drive letters.
/// </summary>
public static class BamReader
{
    public static List<(string Path, DateTime LastRunUtc)> Read()
    {
        var result = new List<(string, DateTime)>();
        var devices = DeviceToDrive();
        foreach (string root in new[] { @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings", @"SYSTEM\CurrentControlSet\Services\bam\UserSettings" })
        {
            using var settings = Registry.LocalMachine.OpenSubKey(root);
            if (settings is null) continue;
            foreach (string sid in settings.GetSubKeyNames())
            {
                using var user = settings.OpenSubKey(sid);
                if (user is null) continue;
                foreach (string value in user.GetValueNames())
                {
                    if (user.GetValue(value) is not byte[] { Length: >= 8 } data || !value.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) continue;
                    long ft = BinaryPrimitives.ReadInt64LittleEndian(data);
                    if (ft <= 0) continue;
                    DateTime when;
                    try { when = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { continue; }
                    if (ToDrivePath(value, devices) is { } path) result.Add((path, when));
                }
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    internal static string? ToDrivePath(string devicePath, IReadOnlyDictionary<string, string> devices)
    {
        foreach (var (device, drive) in devices)
            if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive + devicePath[device.Length..];
        return null;
    }

    /// <summary>"\Device\HarddiskVolume3" → "C:" for every drive letter.</summary>
    public static Dictionary<string, string> DeviceToDrive()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[1024];
        foreach (var d in DriveInfo.GetDrives())
        {
            string letter = d.Name.TrimEnd('\\');
            uint n = QueryDosDevice(letter, buffer, (uint)buffer.Length);
            if (n > 0) map[new string(buffer, 0, (int)n).Split('\0')[0]] = letter;
        }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryDosDeviceW")]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint max);
}
