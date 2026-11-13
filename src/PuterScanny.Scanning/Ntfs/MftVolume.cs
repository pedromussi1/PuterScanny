using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PuterScanny.Scanning.Ntfs;

/// <summary>
/// Reads an NTFS volume's master file table directly (needs administrator rights). This sees every file on the drive,
/// with exact sizes on disk, hard links and hidden streams, including places Windows won't list even for administrators.
/// Read-only: the volume is opened for reading and nothing is ever written.
/// </summary>
public sealed class MftVolume : IDisposable
{
    private readonly SafeFileHandle _volume;
    private readonly List<(long Lcn, long Clusters)> _mftRuns;

    public int BytesPerSector { get; }
    public int ClusterSize { get; }
    public int RecordSize { get; }
    public long MftBytes { get; }
    public long RecordCount => MftBytes / RecordSize;

    private MftVolume(SafeFileHandle volume, int bytesPerSector, int clusterSize, int recordSize, List<(long, long)> runs, long mftBytes)
    {
        _volume = volume;
        BytesPerSector = bytesPerSector;
        ClusterSize = clusterSize;
        RecordSize = recordSize;
        _mftRuns = runs;
        MftBytes = mftBytes;
    }

    /// <summary>Opens the volume a path is on. Returns null (with a reason) when it isn't NTFS or can't be opened.</summary>
    public static MftVolume? TryOpen(string anyPathOnVolume, out string? reason)
    {
        reason = null;
        string? root = Path.GetPathRoot(anyPathOnVolume);
        if (root is null || root.Length < 2 || root[1] != ':')
        {
            reason = "Only local drives with a drive letter can be read directly.";
            return null;
        }
        try
        {
            if (new DriveInfo(root).DriveFormat != "NTFS")
            {
                reason = "The drive isn't NTFS.";
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reason = ex.Message;
            return null;
        }

        var h = CreateFile($@"\\.\{root[0]}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            reason = err == 5 ? "Reading the drive directly needs administrator rights." : $"The drive couldn't be opened (error {err}).";
            return null;
        }

        try
        {
            byte[] boot = new byte[512];
            RandomAccess.Read(h, boot, 0);
            if (boot[3] != 'N' || boot[4] != 'T' || boot[5] != 'F' || boot[6] != 'S')
            {
                reason = "The drive isn't NTFS.";
                h.Dispose();
                return null;
            }
            int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
            int clusterSize = bytesPerSector * boot[0x0D];
            long mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
            sbyte perRecord = (sbyte)boot[0x40];
            int recordSize = perRecord < 0 ? 1 << -perRecord : perRecord * clusterSize;

            // Record 0 is $MFT itself: its data runs say where the rest of the table is.
            byte[] rec0 = new byte[Math.Max(recordSize, bytesPerSector)];
            RandomAccess.Read(h, rec0, mftCluster * clusterSize);
            if (!MftRecordParser.ApplyFixups(rec0.AsSpan(0, recordSize), bytesPerSector) || MftRecordParser.Parse(rec0.AsSpan(0, recordSize), 0) is not { DataRuns: { } runs, Size: > 0 } mft)
            {
                reason = "The drive's file table couldn't be read.";
                h.Dispose();
                return null;
            }
            return new MftVolume(h, bytesPerSector, clusterSize, recordSize, runs, mft.Size);
        }
        catch (IOException ex)
        {
            reason = ex.Message;
            h.Dispose();
            return null;
        }
    }

    /// <summary>Reads every record of the file table in order (base and extension records, in use or not).</summary>
    public IEnumerable<MftRecordInfo> ReadAll(CancellationToken ct, Action<long, long>? progress = null)
    {
        const int chunkRecords = 4096;
        byte[] buffer = new byte[chunkRecords * RecordSize];
        long recordNumber = 0, total = RecordCount;

        foreach (var (lcn, clusters) in _mftRuns)
        {
            if (lcn < 0) { recordNumber += clusters * ClusterSize / RecordSize; continue; } // sparse: no records stored
            long offset = lcn * ClusterSize;
            long remaining = clusters * ClusterSize;
            while (remaining > 0 && recordNumber < total)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = 0;
                while (read < toRead)
                {
                    int n = RandomAccess.Read(_volume, buffer.AsSpan(read, toRead - read), offset + read);
                    if (n <= 0) break;
                    read += n;
                }
                for (int i = 0; i + RecordSize <= read && recordNumber < total; i += RecordSize, recordNumber++)
                {
                    var rec = buffer.AsSpan(i, RecordSize);
                    if (!MftRecordParser.ApplyFixups(rec, BytesPerSector)) continue;
                    if (MftRecordParser.Parse(rec, (ulong)recordNumber) is { InUse: true } info) yield return info;
                }
                offset += read;
                remaining -= read;
                progress?.Invoke(recordNumber, total);
                if (read == 0) break;
            }
        }
    }

    /// <summary>The file-table record number of a file or folder (from its file ID).</summary>
    public static ulong? RecordNumberOf(string path)
    {
        using var h = CreateFile(Core.IO.LongPath.ToNative(path), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return (((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow) & 0x0000FFFFFFFFFFFF;
    }

    public void Dispose() => _volume.Dispose();

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
}
