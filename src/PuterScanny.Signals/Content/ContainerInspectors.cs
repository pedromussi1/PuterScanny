using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PuterScanny.Signals.Content;

/// <summary>Looks at the file names inside a ZIP to tell Office documents, app packages, Java/Android apps, e-books etc. apart.</summary>
internal static class ZipInspector
{
    private const int MaxEntries = 4000;

    public static DetectedFormat? Refine(SafeFileHandle handle, long size, string? extension)
    {
        var names = ReadEntryNames(handle, size);
        if (names is null || names.Count == 0) return null;

        bool Has(string name) => names.Contains(name);
        bool Starts(string prefix) => names.Any(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        bool Ends(string suffix) => names.Any(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (Has("[Content_Types].xml"))
        {
            if (Starts("word/")) return DetectedFormat.Specific("docx", "Word document", "document", ".docx", ".docm", ".dotx", ".dotm");
            if (Starts("xl/")) return DetectedFormat.Specific("xlsx", "Excel workbook", "document", ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlsb", ".xlam");
            if (Starts("ppt/")) return DetectedFormat.Specific("pptx", "PowerPoint presentation", "document", ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm");
            if (Starts("visio/")) return DetectedFormat.Specific("vsdx", "Visio drawing", "document", ".vsdx", ".vsdm", ".vssx", ".vstx");
            if (Has("AppxManifest.xml") || Has("AppxBlockMap.xml"))
                return DetectedFormat.Specific("msix", "Windows app package", "installer", ".appx", ".msix", ".appxupload", ".msixupload");
            if (Has("AppxMetadata/AppxBundleManifest.xml"))
                return DetectedFormat.Specific("msixbundle", "Windows app bundle", "installer", ".appxbundle", ".msixbundle");
            if (Has("extension.vsixmanifest")) return DetectedFormat.Specific("vsix", "Visual Studio extension", "installer", ".vsix");
            if (Ends(".nuspec")) return DetectedFormat.Specific("nupkg", "NuGet package", "package", ".nupkg", ".snupkg");
            if (Has("3D/3dmodel.model")) return DetectedFormat.Specific("3mf", "3D printing project", "3d", ".3mf");
            if (Starts("xps/") || Has("FixedDocSeq.fdseq") || Starts("Documents/1/")) return DetectedFormat.Specific("xps", "XPS document", "document", ".xps", ".oxps");
        }

        if (Has("AndroidManifest.xml") || Has("classes.dex"))
            return DetectedFormat.Specific("apk", "Android app package", "installer", ".apk", ".aab", ".apks", ".xapk", ".jar", ".zip");
        if (Has("mimetype") && Has("META-INF/container.xml"))
            return DetectedFormat.Specific("epub", "E-book (EPUB)", "ebook", ".epub", ".kepub");
        if (Has("mimetype") && Has("content.xml"))
            return DetectedFormat.Specific("odf", "OpenDocument file", "document", ".odt", ".ods", ".odp", ".odg", ".ott", ".ots", ".otp", ".odf");
        if (Has("META-INF/MANIFEST.MF"))
            return DetectedFormat.Specific("jar", "Java program/library (JAR)", "library", ".jar", ".war", ".ear", ".jmod", ".zip", ".apk", ".aar");
        if (Starts("Payload/") && Ends(".app/Info.plist"))
            return DetectedFormat.Specific("ipa", "iPhone app package", "installer", ".ipa");
        if (Ends(".dist-info/WHEEL")) return DetectedFormat.Specific("whl", "Python wheel package", "package", ".whl");
        if (Has("manifest.json") && extension is ".crx" or ".xpi")
            return DetectedFormat.Specific("browser-extension", "Browser extension", "installer", ".crx", ".xpi");
        if (Has("doc.kml")) return DetectedFormat.Specific("kmz", "Map data (Google Earth)", "data", ".kmz");
        if (Has("Index/Document.iwa")) return DetectedFormat.Specific("iwork", "Apple iWork document", "document", ".pages", ".numbers", ".key");
        if (names.All(n => n.EndsWith('/') || IsImage(n)) && extension is ".cbz")
            return DetectedFormat.Specific("cbz", "Comic book archive", "ebook", ".cbz");

        return null;
    }

    private static bool IsImage(string n) =>
        n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads names from the ZIP central directory (at the end of the file).</summary>
    internal static HashSet<string>? ReadEntryNames(SafeFileHandle handle, long size)
    {
        if (size < 22) return null;
        int tailLen = (int)Math.Min(size, 22 + 65535);
        byte[] tail = new byte[tailLen];
        if (SafeFileReader.ReadAt(handle, size - tailLen, tail) != tailLen) return null;

        int eocd = -1;
        for (int i = tailLen - 22; i >= 0; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocd = i; break; }
        }
        if (eocd < 0) return null;

        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
        {
            // ZIP64: the locator sits just before the EOCD record.
            int loc = eocd - 20;
            if (loc < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(loc)) != 0x07064B50) return null;
            long eocd64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(loc + 8));
            Span<byte> rec = stackalloc byte[56];
            if (SafeFileReader.ReadAt(handle, eocd64, rec) != 56 || BinaryPrimitives.ReadUInt32LittleEndian(rec) != 0x06064B50) return null;
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec[40..]);
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec[48..]);
        }
        if (cdOffset < 0 || cdOffset >= size) return null;

        int readLen = (int)Math.Min(Math.Min(cdSize, size - cdOffset), 4 * 1024 * 1024);
        byte[] cd = new byte[readLen];
        int got = SafeFileReader.ReadAt(handle, cdOffset, cd);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        while (pos + 46 <= got && names.Count < MaxEntries)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(pos)) != 0x02014B50) break;
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(pos + 28));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(pos + 30));
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(pos + 32));
            if (pos + 46 + nameLen > got) break;
            names.Add(Encoding.UTF8.GetString(cd, pos + 46, nameLen).Replace('\\', '/'));
            pos += 46 + nameLen + extraLen + commentLen;
        }
        return names;
    }
}

/// <summary>Reads the first directory sector of an OLE compound file to tell Word/Excel/PowerPoint/MSI/Outlook files apart.</summary>
internal static class OleInspector
{
    private static readonly Guid MsiClsid = new("000C1084-0000-0000-C000-000000000046");
    private static readonly Guid MspClsid = new("000C1086-0000-0000-C000-000000000046");
    private static readonly Guid MstClsid = new("000C1082-0000-0000-C000-000000000046");

    public static DetectedFormat? Refine(SafeFileHandle handle, ReadOnlySpan<byte> head)
    {
        if (head.Length < 512) return null;
        int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(head[0x1E..]);
        if (sectorShift is not (9 or 12)) return null;
        int sectorSize = 1 << sectorShift;
        uint firstDirSector = BinaryPrimitives.ReadUInt32LittleEndian(head[0x30..]);
        if (firstDirSector >= 0xFFFFFFFA) return null;

        long offset = (firstDirSector + 1L) * sectorSize;
        byte[] dir = new byte[sectorSize];
        if (SafeFileReader.ReadAt(handle, offset, dir) != sectorSize) return null;

        var names = new List<string>();
        Guid rootClsid = Guid.Empty;
        for (int i = 0; i + 128 <= sectorSize; i += 128)
        {
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(i + 64));
            byte type = dir[i + 66];
            if (nameLen is < 2 or > 64 || type == 0) continue;
            names.Add(Encoding.Unicode.GetString(dir, i, nameLen - 2));
            if (type == 5) rootClsid = new Guid(dir.AsSpan(i + 80, 16));
        }

        if (rootClsid == MsiClsid) return DetectedFormat.Specific("msi", "Windows Installer package", "installer", ".msi");
        if (rootClsid == MspClsid) return DetectedFormat.Specific("msp", "Windows Installer patch", "installer", ".msp");
        if (rootClsid == MstClsid) return DetectedFormat.Specific("mst", "Windows Installer transform", "installer", ".mst");

        bool Has(string n) => names.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
        if (Has("WordDocument")) return DetectedFormat.Specific("doc", "Word 97-2003 document", "document", ".doc", ".dot", ".wps", ".rtf");
        if (Has("Workbook") || Has("Book")) return DetectedFormat.Specific("xls", "Excel 97-2003 workbook", "document", ".xls", ".xlt", ".xla", ".xlsb");
        if (Has("PowerPoint Document")) return DetectedFormat.Specific("ppt", "PowerPoint 97-2003 presentation", "document", ".ppt", ".pps", ".pot");
        if (Has("VisioDocument")) return DetectedFormat.Specific("vsd", "Visio 2003-2010 drawing", "document", ".vsd", ".vss", ".vst");
        if (Has("Quill")) return DetectedFormat.Specific("pub", "Publisher document", "document", ".pub");
        if (names.Any(n => n.StartsWith("__substg1.0_", StringComparison.Ordinal) || n.StartsWith("__properties_version1.0", StringComparison.Ordinal)))
            return DetectedFormat.Specific("msg", "Outlook email message", "email", ".msg", ".oft");
        if (Has("Catalog")) return DetectedFormat.Specific("thumbs-db", "Thumbnail cache (Thumbs.db)", "cache", ".db");
        return null;
    }
}
