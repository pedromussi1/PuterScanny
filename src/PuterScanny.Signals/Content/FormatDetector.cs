using System.Text;
using Microsoft.Win32.SafeHandles;
using PuterScanny.Knowledge;

namespace PuterScanny.Signals.Content;

/// <summary>A format recognised from a file's bytes.</summary>
public sealed record DetectedFormat(string Id, string Name, string Category, bool IsContainer, IReadOnlyCollection<string> Extensions)
{
    public static DetectedFormat Specific(string id, string name, string category, params string[] extensions) =>
        new(id, name, category, false, extensions);
}

/// <summary>
/// Works out what a file really is from its first bytes (and, for ZIP and Office/MSI containers, a look at
/// what is inside), independent of its name.
/// </summary>
public sealed class FormatDetector
{
    public const int HeadSize = 8 * 1024;

    private static readonly HashSet<string> BinaryOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".ico",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mp3", ".flac", ".wav", ".m4a",
        ".zip", ".rar", ".7z", ".gz", ".cab", ".iso", ".exe", ".dll", ".sys", ".msi", ".pdf",
        ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".ttf", ".otf", ".vhdx", ".vmdk",
    };

    private readonly KnowledgeBase _kb;
    private readonly List<MagicSignature> _headSignatures;
    private readonly List<MagicSignature> _farSignatures;

    public FormatDetector(KnowledgeBase kb)
    {
        _kb = kb;
        _headSignatures = kb.Signatures.Where(s => s.RequiredLength <= HeadSize).ToList();
        _farSignatures = kb.Signatures.Where(s => s.RequiredLength > HeadSize).ToList();
        _byId = kb.Signatures.ToDictionary(s => s.Id, StringComparer.Ordinal);
    }

    private readonly Dictionary<string, MagicSignature> _byId;

    /// <summary>Returns the detected format, or null for unrecognised binary data.</summary>
    public DetectedFormat? Detect(SafeFileHandle handle, long fileSize, string? extension, ReadOnlySpan<byte> head)
    {
        MagicSignature? best = null;
        int bestScore = -1;
        foreach (var sig in _headSignatures)
        {
            if (!sig.Matches(head)) continue;
            if (sig.IsWeak && (extension is null || !sig.Extensions.Contains(extension))) continue;
            int score = sig.Pattern.Count(b => b is not null);
            if (score > bestScore)
            {
                best = sig;
                bestScore = score;
            }
        }

        if (best is null)
        {
            foreach (var sig in _farSignatures)
            {
                if (fileSize < sig.RequiredLength) continue;
                byte[] probe = new byte[sig.Pattern.Length];
                if (SafeFileReader.ReadAt(handle, sig.Offset, probe) == probe.Length && Matches(sig, probe))
                {
                    best = sig;
                    break;
                }
            }
        }

        if (best is not null)
        {
            // Java classes and macOS universal binaries share the CA FE BA BE header: Java stores its class-file
            // version (45 or higher) in bytes 6-7, a Mach-O universal binary stores a small architecture count.
            if (best.Id is "class" or "macho-fat" && head.Length >= 8)
            {
                int version = (head[6] << 8) | head[7];
                string want = version is >= 45 and < 100 ? "class" : "macho-fat";
                best = _byId.GetValueOrDefault(want) ?? best;
            }

            var format = ToFormat(best);
            return best.Id switch
            {
                "zip" or "zip-empty" or "zip-spanned" => ZipInspector.Refine(handle, fileSize, extension) ?? format,
                "ole" => OleInspector.Refine(handle, head) ?? format,
                "pem" => RefinePem(head) ?? format,
                _ => format,
            };
        }

        return LooksLikeText(head) ? TextFormat(head, extension) : null;
    }

    /// <summary>
    /// True when the content clearly contradicts the extension: for example a program named ".jpg", or text
    /// pretending to be a video. Containers (ZIP, XML, Office/MSI compound files) hold many legitimate formats,
    /// so they never count as a mismatch on their own.
    /// </summary>
    public bool IsMismatch(DetectedFormat format, string? extension)
    {
        if (string.IsNullOrEmpty(extension) || format.IsContainer) return false;
        if (format.Extensions.Contains(extension)) return false;
        if (format.Id == "text") return BinaryOnlyExtensions.Contains(extension);
        if (format.Id is "empty" or "zero-filled") return false;
        return _kb.GetType(extension) is not null; // only judge extensions we actually know
    }

    private static bool Matches(MagicSignature sig, ReadOnlySpan<byte> probe)
    {
        for (int i = 0; i < sig.Pattern.Length; i++)
            if (sig.Pattern[i] is byte b && probe[i] != b) return false;
        return true;
    }

    private static DetectedFormat ToFormat(MagicSignature s) => new(s.Id, s.Name, s.Category, s.IsContainer, s.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static DetectedFormat? RefinePem(ReadOnlySpan<byte> head)
    {
        string text = Encoding.ASCII.GetString(head[..Math.Min(head.Length, 200)]);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            return DetectedFormat.Specific("private-key", "Private key (keep safe!)", "certificate", ".pem", ".key", ".ppk", "");
        if (text.Contains("CERTIFICATE", StringComparison.Ordinal))
            return DetectedFormat.Specific("pem-certificate", "Security certificate (PEM)", "certificate", ".pem", ".crt", ".cer", ".cert", "");
        return null;
    }

    private DetectedFormat TextFormat(ReadOnlySpan<byte> head, string? extension)
    {
        string category = "text";
        var known = _kb.GetType(extension);
        if (known is not null && known.Category is "code" or "script" or "config" or "web" or "log" or "data" or "subtitle" or "text" or "document" or "certificate" or "email" or "3d" or "design" or "database" or "shortcut" or "temporary" or "backup")
            category = known.Category;

        string encoding = head.Length >= 2 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF) ? " (Unicode)" : "";
        return new DetectedFormat("text", "Text" + encoding, category, false, []);
    }

    /// <summary>Plain-text heuristic: UTF-16 with a byte-order mark, or no NUL bytes and almost no control characters.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        if (head.Length >= 2 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF)) return true;

        int control = 0;
        foreach (byte b in head)
        {
            if (b == 0) return false;
            if (b < 0x09 || b is > 0x0D and < 0x20 and not 0x1B) control++;
        }
        if (control * 100 > head.Length) return false;

        // Reject byte soup that is not valid UTF-8 (allow a cut-off final character).
        var span = head;
        int cut = Math.Min(3, span.Length);
        for (int i = 0; i < cut; i++)
        {
            byte b = span[^1];
            if (b < 0x80) break;
            span = span[..^1];
            if ((b & 0xC0) == 0xC0) break;
        }
        return System.Text.Unicode.Utf8.IsValid(span) || span.Length < 16;
    }
}
