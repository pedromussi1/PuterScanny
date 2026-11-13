using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PuterScanny.Core.IO;
using PuterScanny.Core.Models;
using static PuterScanny.Signals.Native.WinTrust;

namespace PuterScanny.Signals.Content;

/// <summary>
/// Checks a file's digital signature the way Windows does (WinVerifyTrust), including the catalog signatures
/// most Windows system files use. Fully offline: revocation and online certificate lookups are disabled.
/// Safe to call from many threads at once.
/// </summary>
public sealed class AuthenticodeVerifier : IDisposable
{
    private static readonly IntPtr NoUi = new(-1);

    private readonly ThreadLocal<CatalogAdmins> _admins = new(() => new CatalogAdmins(), trackAllValues: true);

    public sealed record Result(SignatureState State, SignatureKind Kind, string? SignedBy, int ErrorCode);

    public static readonly Result NotChecked = new(SignatureState.NotChecked, SignatureKind.None, null, 0);

    public Result Verify(SafeFileHandle handle, string path, bool hasEmbeddedSignature)
    {
        string native = LongPath.ToNative(path);
        Result? embedded = null;
        if (hasEmbeddedSignature)
        {
            embedded = VerifyEmbedded(handle, native);
            if (embedded.State is not SignatureState.NotSigned) return embedded;
        }

        var catalog = VerifyCatalog(handle, native);
        if (catalog is not null) return catalog;

        return embedded ?? new Result(SignatureState.NotSigned, SignatureKind.None, null, TRUST_E_NOSIGNATURE);
    }

    private static Result VerifyEmbedded(SafeFileHandle handle, string nativePath)
    {
        SafeFileReader.Rewind(handle);
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = nativePath,
            hFile = handle.DangerousGetHandle(),
        };

        IntPtr pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(fileInfo, pInfo, false);
            var (hr, signer) = Run(WTD_CHOICE_FILE, pInfo);
            return new Result(Map(hr), SignatureKind.Embedded, signer, hr);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pInfo);
            Marshal.FreeHGlobal(pInfo);
            if (addedRef) handle.DangerousRelease();
        }
    }

    /// <summary>Returns null when the file is not listed in any Windows catalog.</summary>
    private Result? VerifyCatalog(SafeFileHandle handle, string nativePath)
    {
        var admins = _admins.Value!;
        foreach (IntPtr admin in admins.Handles)
        {
            if (admin == IntPtr.Zero) continue;

            SafeFileReader.Rewind(handle);
            uint hashLen = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref hashLen, null, 0);
            if (hashLen is 0 or > 256) continue;
            byte[] hash = new byte[hashLen];
            SafeFileReader.Rewind(handle);
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref hashLen, hash, 0)) continue;

            IntPtr catInfo = CryptCATAdminEnumCatalogFromHash(admin, hash, hashLen, 0, IntPtr.Zero);
            if (catInfo == IntPtr.Zero) continue;

            try
            {
                var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (!CryptCATCatalogInfoFromContext(catInfo, ref ci, 0)) continue;

                GCHandle pinned = GCHandle.Alloc(hash, GCHandleType.Pinned);
                IntPtr pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                bool addedRef = false;
                try
                {
                    handle.DangerousAddRef(ref addedRef);
                    var cat = new WINTRUST_CATALOG_INFO
                    {
                        cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                        pcwszCatalogFilePath = ci.wszCatalogFile,
                        pcwszMemberTag = Convert.ToHexString(hash),
                        pcwszMemberFilePath = nativePath,
                        hMemberFile = handle.DangerousGetHandle(),
                        pbCalculatedFileHash = pinned.AddrOfPinnedObject(),
                        cbCalculatedFileHash = hashLen,
                        hCatAdmin = admin,
                    };
                    Marshal.StructureToPtr(cat, pInfo, false);
                    SafeFileReader.Rewind(handle);
                    var (hr, signer) = Run(WTD_CHOICE_CATALOG, pInfo);
                    Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(pInfo);
                    return new Result(Map(hr), SignatureKind.Catalog, signer, hr);
                }
                finally
                {
                    Marshal.FreeHGlobal(pInfo);
                    pinned.Free();
                    if (addedRef) handle.DangerousRelease();
                }
            }
            finally
            {
                CryptCATAdminReleaseCatalogContext(admin, catInfo, 0);
            }
        }
        return null;
    }

    private static (int Hr, string? Signer) Run(uint unionChoice, IntPtr pInfo)
    {
        var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = unionChoice,
            pInfo = pInfo,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_DISABLE_MD2_MD4,
        };

        int hr = WinVerifyTrust(NoUi, ref action, ref data);
        string? signer = null;
        try
        {
            if (data.hWVTStateData != IntPtr.Zero) signer = ReadSigner(data.hWVTStateData);
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(NoUi, ref action, ref data);
        }
        return (hr, signer);
    }

    private static string? ReadSigner(IntPtr stateData)
    {
        IntPtr provData = WTHelperProvDataFromStateData(stateData);
        if (provData == IntPtr.Zero) return null;
        IntPtr signer = WTHelperGetProvSignerFromChain(provData, 0, false, 0);
        if (signer == IntPtr.Zero) return null;

        // CRYPT_PROVIDER_SGNR: cbStruct(4) + FILETIME(8) + csCertChain(4) + pasCertChain(ptr at 16)
        int chainCount = Marshal.ReadInt32(signer, 12);
        IntPtr chain = Marshal.ReadIntPtr(signer, 16);
        if (chainCount <= 0 || chain == IntPtr.Zero) return null;

        // CRYPT_PROVIDER_CERT: cbStruct(4) + pCert (pointer-aligned)
        IntPtr cert = Marshal.ReadIntPtr(chain, IntPtr.Size);
        if (cert == IntPtr.Zero) return null;

        string? name = CertName(cert, Native.Crypt32.CERT_NAME_SIMPLE_DISPLAY_TYPE, IntPtr.Zero);
        string? org = null;
        IntPtr oid = Marshal.StringToHGlobalAnsi("2.5.4.10"); // organization (O=)
        try { org = CertName(cert, Native.Crypt32.CERT_NAME_ATTR_TYPE, oid); }
        finally { Marshal.FreeHGlobal(oid); }

        if (name is null) return org;
        if (org is null || name.Contains(org, StringComparison.OrdinalIgnoreCase) || org.Contains(name, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} ({org})";
    }

    private static string? CertName(IntPtr cert, uint type, IntPtr para)
    {
        uint len = Native.Crypt32.CertGetNameString(cert, type, 0, para, null, 0);
        if (len <= 1) return null;
        char[] buf = new char[len];
        Native.Crypt32.CertGetNameString(cert, type, 0, para, buf, len);
        string s = new string(buf, 0, (int)len - 1).Trim();
        return s.Length == 0 ? null : s;
    }

    private static SignatureState Map(int hr) => hr switch
    {
        0 => SignatureState.Valid,
        TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN => SignatureState.NotSigned,
        TRUST_E_BAD_DIGEST => SignatureState.Tampered,
        CERT_E_EXPIRED => SignatureState.Expired,
        CERT_E_REVOKED => SignatureState.Revoked,
        CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING or TRUST_E_EXPLICIT_DISTRUST or CERT_E_UNTRUSTEDTESTROOT
            or TRUST_E_SUBJECT_NOT_TRUSTED or TRUST_E_CERT_SIGNATURE or TRUST_E_NO_SIGNER_CERT => SignatureState.Untrusted,
        _ => SignatureState.Error,
    };

    public void Dispose()
    {
        foreach (var a in _admins.Values) a.Dispose();
        _admins.Dispose();
    }

    /// <summary>Per-thread catalog lookup contexts: SHA-256 catalogs first (current Windows), then SHA-1 (older ones).</summary>
    private sealed class CatalogAdmins : IDisposable
    {
        public readonly IntPtr[] Handles = new IntPtr[2];

        public CatalogAdmins()
        {
            if (!CryptCATAdminAcquireContext2(out Handles[0], IntPtr.Zero, "SHA256", IntPtr.Zero, 0)) Handles[0] = IntPtr.Zero;
            if (!CryptCATAdminAcquireContext2(out Handles[1], IntPtr.Zero, null, IntPtr.Zero, 0)) Handles[1] = IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var h in Handles)
                if (h != IntPtr.Zero) CryptCATAdminReleaseContext(h, 0);
        }
    }
}
