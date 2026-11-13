using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PuterScanny.Core.IO;
using PuterScanny.Core.Models;
using static PuterScanny.Signals.Native.Kernel32;

namespace PuterScanny.Signals.Content;

/// <summary>
/// Opens files for inspection without side effects: shares access with everyone, never triggers cloud
/// downloads (OPEN_NO_RECALL), and, where Windows allows it, tells NTFS not to update the file's
/// "last accessed" time so our reading never disguises how recently you used something.
/// </summary>
public static class SafeFileReader
{
    private const uint ShareAll = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    public static (SafeFileHandle? Handle, ReadStatus Status) Open(string path)
    {
        string native = LongPath.ToNative(path);

        // First choice: read + write-attributes, so the access-time freeze below is allowed.
        var handle = CreateFile(native, GENERIC_READ | FILE_WRITE_ATTRIBUTES, ShareAll, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_NO_RECALL, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            long dontTouch = -1;
            SetFileTime(handle, IntPtr.Zero, ref dontTouch, IntPtr.Zero);
            return (handle, ReadStatus.Read);
        }
        handle.Dispose();

        // Second choice: read-only (common for system-protected files).
        handle = CreateFile(native, GENERIC_READ, ShareAll, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_NO_RECALL, IntPtr.Zero);
        if (!handle.IsInvalid) return (handle, ReadStatus.Read);

        int err = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return (null, err switch
        {
            ERROR_ACCESS_DENIED => ReadStatus.AccessDenied,
            ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => ReadStatus.InUse,
            _ => ReadStatus.Error,
        });
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes at an offset; returns the count read.</summary>
    public static int ReadAt(SafeFileHandle handle, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public static void Rewind(SafeFileHandle handle) => SetFilePointerEx(handle, 0, IntPtr.Zero, 0);
}
