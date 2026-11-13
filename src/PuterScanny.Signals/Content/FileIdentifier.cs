using PuterScanny.Core.Models;
using PuterScanny.Knowledge;

namespace PuterScanny.Signals.Content;

/// <summary>
/// Looks inside one file and says what it really is: its true format (ignoring the name), and for programs
/// their publisher, version and digital signature. Thread-safe; one instance is shared by all workers.
/// </summary>
public sealed class FileIdentifier : IDisposable
{
    private readonly KnowledgeBase _kb;
    private readonly FormatDetector _detector;
    private readonly AuthenticodeVerifier _verifier = new();

    /// <summary>Program files at least this big are read in full for their signature check one at a time (gentle mode).</summary>
    public const long LargeFileBytes = 64L * 1024 * 1024;

    private readonly SemaphoreSlim? _largeFileLane;

    /// <param name="oneLargeFileAtATime">Only let one worker read a huge program file in full at any moment.</param>
    public FileIdentifier(KnowledgeBase kb, bool verifySignatures = true, bool oneLargeFileAtATime = false)
    {
        _kb = kb;
        _detector = new FormatDetector(kb);
        VerifySignatures = verifySignatures;
        if (oneLargeFileAtATime) _largeFileLane = new SemaphoreSlim(1, 1);
    }

    private AuthenticodeVerifier.Result VerifyPaced(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, long size, bool hasEmbedded)
    {
        if (_largeFileLane is null || size < LargeFileBytes) return _verifier.Verify(handle, path, hasEmbedded);
        _largeFileLane.Wait();
        try { return _verifier.Verify(handle, path, hasEmbedded); }
        finally { _largeFileLane.Release(); }
    }

    public bool VerifySignatures { get; }

    public ContentIdentity Identify(long entryId, string path, long size, string? extension, EntryFlags flags)
    {
        string? extCategory = _kb.GetType(extension)?.Category;

        if (flags.HasFlag(EntryFlags.CloudPlaceholder))
            return new ContentIdentity { EntryId = entryId, ReadStatus = ReadStatus.Skipped, Category = extCategory };
        if (size == 0)
            return new ContentIdentity { EntryId = entryId, ReadStatus = ReadStatus.Skipped, FormatId = "empty", FormatName = "Empty file (0 bytes)", Category = extCategory ?? "other" };

        var (handle, status) = SafeFileReader.Open(path);
        if (handle is null)
            return new ContentIdentity { EntryId = entryId, ReadStatus = status, Category = extCategory };

        using (handle)
        {
            try
            {
                byte[] head = new byte[(int)Math.Min(size, FormatDetector.HeadSize)];
                int read = SafeFileReader.ReadAt(handle, 0, head);
                var headSpan = head.AsSpan(0, read);

                DetectedFormat? format = _detector.Detect(handle, size, extension, headSpan);
                if (format is null)
                {
                    return new ContentIdentity
                    {
                        EntryId = entryId,
                        ReadStatus = ReadStatus.Read,
                        FormatId = "binary",
                        FormatName = "Binary data (format not recognised)",
                        Category = extCategory ?? "other",
                    };
                }

                PeInfo? pe = null;
                if (format.Id == "pe")
                {
                    var peResult = PeReader.Read(handle, size);
                    if (peResult is { IsValidPe: true })
                    {
                        pe = peResult.Info;
                        format = DescribePe(pe, extension);
                        if (VerifySignatures)
                        {
                            var sig = VerifyPaced(handle, path, size, pe.HasEmbeddedSignature);
                            pe = pe with { SignatureState = sig.State, SignatureKind = sig.Kind, SignedBy = sig.SignedBy, SignatureError = sig.ErrorCode };
                        }
                    }
                    else
                    {
                        pe = peResult?.Info;
                        format = DetectedFormat.Specific("dos", "MS-DOS program or stub", "executable", ".exe", ".com", ".dll", ".sys", ".ovl");
                    }
                }
                else if (format.Id is "msi" or "msp" && VerifySignatures)
                {
                    var sig = VerifyPaced(handle, path, size, hasEmbedded: true);
                    pe = new PeInfo { SignatureState = sig.State, SignatureKind = sig.Kind, SignedBy = sig.SignedBy, SignatureError = sig.ErrorCode, Machine = "", Subsystem = "" };
                }

                return new ContentIdentity
                {
                    EntryId = entryId,
                    ReadStatus = ReadStatus.Read,
                    FormatId = format.Id,
                    FormatName = format.Name,
                    Category = format.Id == "text" ? format.Category : format.Category ?? extCategory,
                    ExtensionMismatch = _detector.IsMismatch(format, extension),
                    Pe = pe,
                };
            }
            catch (IOException)
            {
                return new ContentIdentity { EntryId = entryId, ReadStatus = ReadStatus.Error, Category = extCategory };
            }
            catch (UnauthorizedAccessException)
            {
                return new ContentIdentity { EntryId = entryId, ReadStatus = ReadStatus.AccessDenied, Category = extCategory };
            }
        }
    }

    private static readonly string[] ProgramExtensions =
        [".exe", ".com", ".scr", ".cpl", ".efi", ".pif", ".msc"];
    private static readonly string[] LibraryExtensions =
        [".dll", ".ocx", ".drv", ".mui", ".mun", ".ax", ".acm", ".ime", ".tsp", ".winmd", ".pyd", ".node", ".tlb", ".olb", ".fon", ".so", ".cpl", ".rll", ".api", ".8bf", ".xll", ".bpl", ".flt", ".iec", ".aex", ".vst", ".vst3", ".ppi", ".dat", ".bin"];
    private static readonly string[] DriverExtensions = [".sys", ".drv", ".efi", ".exe", ".dll"];

    private static DetectedFormat DescribePe(PeInfo pe, string? extension)
    {
        if (pe.IsDriver || extension == ".sys")
            return new DetectedFormat("pe-driver", pe.Subsystem.StartsWith("Native", StringComparison.Ordinal) && extension == ".exe" ? "Native Windows program" : "Driver", "driver", false, DriverExtensions);
        if (pe.Subsystem.StartsWith("UEFI", StringComparison.Ordinal))
            return new DetectedFormat("pe-efi", "UEFI firmware program", "driver", false, [".efi", ".exe", ".dll", ".bin"]);
        if (pe.IsDll)
            return new DetectedFormat("pe-dll", pe.IsDotNet ? ".NET program component (DLL)" : "Program component (DLL)", "library", false, LibraryExtensions);
        return new DetectedFormat("pe-exe", pe.IsDotNet ? ".NET program" : "Windows program", "executable", false, ProgramExtensions);
    }

    public void Dispose()
    {
        _verifier.Dispose();
        _largeFileLane?.Dispose();
    }
}
