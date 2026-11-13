using System.IO.Compression;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Signals.Content;
using PuterScanny.Signals.Inventory;

namespace PuterScanny.Tests;

public sealed class FileIdentifierTests : IDisposable
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();
    private readonly TempTree _tree = new();
    private readonly FileIdentifier _id = new(Kb);

    private ContentIdentity Identify(string path)
    {
        var info = new FileInfo(path);
        string? ext = info.Extension.Length == 0 ? null : info.Extension.ToLowerInvariant();
        return _id.Identify(1, path, info.Length, ext, EntryFlags.None);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_tree.Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    [Fact]
    public void Recognises_content_regardless_of_name()
    {
        var png = Identify(Write("photo.png", PngHeader));
        Assert.Equal("png", png.FormatId);
        Assert.Equal("image", png.Category);
        Assert.False(png.ExtensionMismatch);

        var renamed = Identify(Write("photo.pdf", PngHeader));
        Assert.Equal("png", renamed.FormatId);
        Assert.True(renamed.ExtensionMismatch);
    }

    [Fact]
    public void A_program_disguised_as_a_picture_is_flagged()
    {
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (!File.Exists(notepad)) return;
        string fake = Path.Combine(_tree.Root, "holiday.jpg");
        File.Copy(notepad, fake);

        var id = Identify(fake);
        Assert.Equal("pe-exe", id.FormatId);
        Assert.Equal("executable", id.Category);
        Assert.True(id.ExtensionMismatch);
    }

    [Fact]
    public void Office_documents_are_told_apart_inside_zip()
    {
        string path = Path.Combine(_tree.Root, "report.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry("word/document.xml");
        }
        var id = Identify(path);
        Assert.Equal("docx", id.FormatId);
        Assert.Equal("Word document", id.FormatName);
        Assert.False(id.ExtensionMismatch);

        File.Copy(path, Path.Combine(_tree.Root, "report.xlsx"));
        Assert.True(Identify(Path.Combine(_tree.Root, "report.xlsx")).ExtensionMismatch);

        string plainZip = Path.Combine(_tree.Root, "stuff.zip");
        using (var zip = ZipFile.Open(plainZip, ZipArchiveMode.Create)) zip.CreateEntry("a.txt");
        Assert.Equal("zip", Identify(plainZip).FormatId);
    }

    [Fact]
    public void Text_and_empty_files()
    {
        var text = Identify(Write("notes.json", "{\"a\": 1}\r\n"u8.ToArray()));
        Assert.Equal("text", text.FormatId);
        Assert.Equal("config", text.Category);
        Assert.False(text.ExtensionMismatch);

        var fakeVideo = Identify(Write("movie.mp4", "this is not a movie"u8.ToArray()));
        Assert.True(fakeVideo.ExtensionMismatch);

        var empty = Identify(Write("empty.txt", []));
        Assert.Equal("empty", empty.FormatId);
        Assert.Equal(ReadStatus.Skipped, empty.ReadStatus);
    }

    [Fact]
    public void Look_alike_headers_are_told_apart()
    {
        // Uncompressed TGA images start like a mouse cursor (00 00 02 00); the extension must decide.
        var tga = Identify(Write("texture.tga", [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 64, 0, 64, 0, 32, 8, 1, 2, 3, 4]));
        Assert.NotEqual("cur", tga.FormatId);
        Assert.False(tga.ExtensionMismatch);

        // Java classes and macOS universal binaries both start with CA FE BA BE.
        var javaClass = Identify(Write("Main.class", [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 0x34, 0, 1, 2, 3]));
        Assert.Equal("class", javaClass.FormatId);
        var macBinary = Identify(Write("libthing.dylib", [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2, 1, 0, 0, 7, 0, 0, 0, 3]));
        Assert.Equal("macho-fat", macBinary.FormatId);
        Assert.False(macBinary.ExtensionMismatch);
    }

    [Fact]
    public void Cloud_placeholders_are_never_opened()
    {
        var id = _id.Identify(1, @"C:\does\not\exist.docx", 1234, ".docx", EntryFlags.CloudPlaceholder);
        Assert.Equal(ReadStatus.Skipped, id.ReadStatus);
        Assert.Equal("document", id.Category);
    }

    [Fact]
    public void Windows_programs_report_publisher_version_and_a_valid_signature()
    {
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (!File.Exists(notepad)) return;

        var id = Identify(notepad);
        Assert.Equal("pe-exe", id.FormatId);
        var pe = Assert.IsType<PeInfo>(id.Pe);
        Assert.Contains("Microsoft", pe.CompanyName);
        Assert.False(string.IsNullOrEmpty(pe.FileVersion));
        Assert.False(pe.IsDll);
        Assert.Equal(SignatureState.Valid, pe.SignatureState);
        Assert.Contains("Microsoft", pe.SignedBy);

        string kernel32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");
        var dll = Identify(kernel32);
        Assert.Equal("pe-dll", dll.FormatId);
        Assert.True(dll.Pe!.IsDll);
    }

    [Fact]
    public void A_modified_signed_program_is_reported_as_tampered()
    {
        // dotnet.exe carries an embedded Authenticode signature (unlike notepad, which is catalog-signed).
        string? signed = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
        }.FirstOrDefault(File.Exists);
        if (signed is null) return;

        var original = Identify(signed);
        Assert.Equal(SignatureState.Valid, original.Pe!.SignatureState);
        Assert.Equal(SignatureKind.Embedded, original.Pe.SignatureKind);
        Assert.Contains("Microsoft", original.Pe.SignedBy ?? "", StringComparison.OrdinalIgnoreCase);

        byte[] bytes = File.ReadAllBytes(signed);
        bytes[bytes.Length / 3] ^= 0xFF; // flip one byte in the signed area
        var tampered = Identify(Write("dotnet-copy.exe", bytes));
        Assert.Equal(SignatureState.Tampered, tampered.Pe!.SignatureState);
    }

    [Fact]
    public void Unsigned_programs_are_reported_as_not_signed()
    {
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (!File.Exists(notepad)) return;
        byte[] bytes = File.ReadAllBytes(notepad);
        bytes[bytes.Length / 2] ^= 0xFF; // no longer matches its catalog entry
        var id = Identify(Write("changed.exe", bytes));
        Assert.Equal(SignatureState.NotSigned, id.Pe!.SignatureState);
    }

    public void Dispose()
    {
        _id.Dispose();
        _tree.Dispose();
    }
}

public sealed class InventoryParsingTests
{
    [Fact]
    public void Steam_keyvalues_are_flattened()
    {
        const string vdf = """
            "AppState"
            {
                "appid"     "620"
                "name"      "Portal 2"
                "installdir"    "Portal 2"
                "UserConfig" { "language" "english" "name" "nested" }
            }
            """;
        var pairs = Vdf.Flatten(vdf).ToList();
        Assert.Contains(new KeyValuePair<string, string>("AppState/name", "Portal 2"), pairs);
        Assert.Contains(new KeyValuePair<string, string>("AppState/UserConfig/name", "nested"), pairs);
    }

    [Fact]
    public void Library_paths_with_escaped_backslashes()
    {
        var pairs = Vdf.Flatten("\"libraryfolders\" { \"0\" { \"path\" \"D:\\\\SteamLibrary\" } }").ToList();
        Assert.Equal(@"D:\SteamLibrary", pairs.Single(p => p.Key.EndsWith("/path")).Value);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\",0", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\uninst.exe /S", @"C:\Program Files\App\uninst.exe")]
    [InlineData("MsiExec.exe /X{1234}", null)]
    public void Uninstall_command_paths_are_extracted(string value, string? expected)
    {
        Assert.Equal(expected, InstalledProgramsCollector.ExtractPath(value));
    }

    [Fact]
    public void Real_installed_programs_can_be_read()
    {
        var apps = InstalledProgramsCollector.Collect(new GenericFolders(PathTokens.ForThisMachine()));
        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        // No program may claim a shared folder such as Program Files itself.
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.DoesNotContain(apps, a => string.Equals(a.InstallLocation?.TrimEnd('\\'), pf, StringComparison.OrdinalIgnoreCase));
    }
}
