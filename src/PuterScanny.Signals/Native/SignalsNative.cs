using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PuterScanny.Signals.Native;

internal static class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_NO_RECALL = 0x00100000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    /// <summary>Passing 0xFFFFFFFF/0xFFFFFFFF as the access time tells NTFS not to update it for this handle.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileTime(SafeFileHandle hFile, IntPtr lpCreationTime, ref long lpLastAccessTime, IntPtr lpLastWriteTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, IntPtr lpNewFilePointer, uint dwMoveMethod);
}

internal static class WinTrust
{
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_CHOICE_CATALOG = 2;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;
    public const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    public const uint WTD_DISABLE_MD2_MD4 = 0x00002000;

    public const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    public const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    public const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    public const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    public const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    public const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
    public const int CERT_E_EXPIRED = unchecked((int)0x800B0101);
    public const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    public const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    public const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    public const int CERT_E_UNTRUSTEDTESTROOT = unchecked((int)0x800B010D);
    public const int CRYPT_E_SECURITY_SETTINGS = unchecked((int)0x80092026);
    public const int CRYPT_E_FILE_ERROR = unchecked((int)0x80092003);
    public const int TRUST_E_NO_SIGNER_CERT = unchecked((int)0x80096002);
    public const int TRUST_E_COUNTER_SIGNER = unchecked((int)0x80096003);
    public const int TRUST_E_CERT_SIGNATURE = unchecked((int)0x80096004);
    public const int TRUST_E_TIME_STAMP = unchecked((int)0x80096005);
    public const int CRYPT_E_BAD_MSG = unchecked((int)0x8009200D);
    public const int NTE_BAD_ALGID = unchecked((int)0x80090008);
    public const int TRUST_E_FAIL = unchecked((int)0x800B010B);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string? pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public string? pcwszCatalogFilePath;
        public string? pcwszMemberTag;
        public string? pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    public static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    public static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    public static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    // ---- catalogs

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile, ref uint pcbHash, [Out] byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    public static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);
}

internal static class Crypt32
{
    public const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;
    public const uint CERT_NAME_ATTR_TYPE = 3;

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, EntryPoint = "CertGetNameStringW")]
    public static extern uint CertGetNameString(IntPtr pCertContext, uint dwType, uint dwFlags, IntPtr pvTypePara, [Out] char[]? pszNameString, uint cchNameString);
}

internal static class Shlwapi
{
    public const uint ASSOCF_NOTRUNCATE = 0x00000020;
    public const uint ASSOCF_INIT_IGNOREUNKNOWN = 0x00000400;
    public const uint ASSOCF_REMAPRUNDLL = 0x00000080;

    public const int ASSOCSTR_FRIENDLYDOCNAME = 3;
    public const int ASSOCSTR_FRIENDLYAPPNAME = 4;
    public const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SHLoadIndirectString")]
    public static extern int SHLoadIndirectString(string pszSource, [Out] char[] pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    public static extern int AssocQueryString(uint flags, int str, string pszAssoc, string? pszExtra, [Out] char[]? pszOut, ref uint pcchOut);
}

internal static class Msi
{
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_UNKNOWN_PRODUCT = 1605;

    public const int INSTALLSTATE_LOCAL = 3;
    public const int INSTALLSTATE_SOURCE = 4;
    public const int INSTALLSTATE_DEFAULT = 5;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumComponentsW")]
    public static extern int MsiEnumComponents(uint iComponentIndex, [Out] char[] lpComponentBuf);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumClientsW")]
    public static extern int MsiEnumClients(string szComponent, uint iProductIndex, [Out] char[] lpProductBuf);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetComponentPathW")]
    public static extern int MsiGetComponentPath(string szProduct, string szComponent, [Out] char[]? lpPathBuf, ref uint pcchBuf);
}

internal static class ProcessImage
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Full path of a running process's program file (works for most processes without administrator rights).</summary>
    public static string? QueryPath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            char[] buf = new char[1024];
            uint size = (uint)buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}

internal static class Sfc
{
    [DllImport("sfc.dll", CharSet = CharSet.Unicode, EntryPoint = "SfcIsFileProtected")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SfcIsFileProtectedNative(IntPtr rpcHandle, string fileName);

    /// <summary>True when Windows Resource Protection guards this file (Windows restores it if changed).</summary>
    public static bool IsProtected(string path)
    {
        try { return SfcIsFileProtectedNative(IntPtr.Zero, path); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}

internal static class KnownFolders
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The real path of a Windows known folder (Program Files, Desktop, …), or null.</summary>
    public static string? GetPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}

internal static class AdminCheck
{
    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
