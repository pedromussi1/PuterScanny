using System.Text;
using System.Text.RegularExpressions;
using PuterScanny.Analysis;
using PuterScanny.Core.Models;
using PuterScanny.Storage;

namespace PuterScanny.Chat;

/// <summary>Files of one kind inside a folder, with a few example names.</summary>
public sealed record KindOfFiles(string Label, string Extension, long Files, long Bytes, IReadOnlyList<string> Examples);

/// <summary>A short piece of a readme (or .nfo) found in or next to an item.</summary>
public sealed record ReadmeExcerpt(string Path, string Name, string Folder, string Text);

/// <summary>A bigger thing an item is part of: a mod download, a leftover folder, a program.</summary>
public sealed record Container(FileSystemEntry Entry, string What, string? Detail);

/// <summary>
/// Everything worth saying about one file or folder beyond its own record: what's inside it, what it's part of,
/// what a readme next to it says, and for programs who made them. Built on demand, read-only.
/// </summary>
public sealed record ItemProfile
{
    public required FileSystemEntry Entry { get; init; }
    public Container? PartOf { get; init; }
    public ModDownload? Mod { get; init; }
    public IReadOnlyList<KindOfFiles> Kinds { get; init; } = [];
    public IReadOnlyList<FileSystemEntry> Subfolders { get; init; } = [];
    public FileSystemEntry? Parent { get; init; }
    public ReadmeExcerpt? Readme { get; init; }
    public PeInfo? Program { get; init; }
}

public sealed partial class ItemProfiler(ScanDatabase db)
{
    private const long MaxReadmeBytes = 64 * 1024;

    public ItemProfile Build(FileSystemEntry e)
    {
        var parent = e.ParentId is long pid ? db.GetEntry(pid) : null;
        var kinds = new List<KindOfFiles>();
        var subfolders = new List<FileSystemEntry>();
        if (e.IsDirectory)
        {
            foreach (var stat in db.GetTypeStats(e.Path, 6))
            {
                var examples = db.GetFilesOfType(e.Path, stat.Label, 12).DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
                string ext = examples.Select(x => x.Extension).FirstOrDefault(x => x is not null) ?? "";
                kinds.Add(new KindOfFiles(stat.Label.Length > 0 ? stat.Label : "unidentified file", ext, stat.Files, stat.Bytes,
                    examples.Select(x => x.Name).ToList()));
            }
            subfolders = db.GetChildren(e.Id, 40).Where(c => c.IsDirectory && c.TotalSize > 0).Take(4).ToList();
        }

        ModDownloads.TryParse(e.Name, e.IsDirectory, out var mod);
        return new ItemProfile
        {
            Entry = e,
            PartOf = PartOf(e),
            Mod = e.Analysis?.Facts.HasFlag(FactFlags.ModDownload) == true ? mod : null,
            Kinds = kinds,
            Subfolders = subfolders,
            Parent = parent,
            Readme = FindReadme(e),
            Program = e.IsDirectory ? null : db.GetPeInfo(e.Id),
        };
    }

    /// <summary>The nearest folder above the item that is something in its own right (a mod download, a leftover, a program).</summary>
    public Container? PartOf(FileSystemEntry e)
    {
        string? ownApp = e.Analysis?.OwnerAppName;
        var current = e;
        for (int i = 0; i < 12 && current.ParentId is long pid; i++)
        {
            var up = db.GetEntry(pid);
            if (up is null || up.Depth == 0) break;
            var a = up.Analysis;
            if (a is not null)
            {
                if (a.Facts.HasFlag(FactFlags.ModDownload) && ModDownloads.TryParse(up.Name, true, out var mod))
                    return new Container(up, "a mod download (Nexus Mods)", ModText(mod));
                if (a.Facts.HasFlag(FactFlags.Leftover))
                    return new Container(up, "a leftover folder from a program that is no longer installed", null);
                if (ownApp is null && a.OwnerAppName is { } app)
                    return new Container(up, $"part of {app}", null);
            }
            current = up;
        }
        return null;
    }

    public static string ModText(ModDownload mod) =>
        $"\"{mod.Name}\", mod #{mod.ModId}, version {mod.Version}, uploaded {mod.UploadedUtc:d MMM yyyy}";

    // ---------------------------------------------------------------- readme

    [GeneratedRegex(@"^(read[ _\-.]?me|leia[ _\-]?me|lisez[ _\-]?moi|install(ation)?|instructions?|how[ _\-]?to|about|info|description|changelog)[^\\]*\.(txt|md|nfo|rtf|1st)$|\.nfo$|^readme$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadmeName();

    /// <summary>A readme in the folder itself, or in the folders above it up to the thing it's part of (three levels at most).</summary>
    private ReadmeExcerpt? FindReadme(FileSystemEntry e)
    {
        var folders = new List<FileSystemEntry>();
        if (e.IsDirectory) folders.Add(e);
        var current = e;
        while (folders.Count < 3 && current.ParentId is long pid && db.GetEntry(pid) is { Depth: > 0 } up)
        {
            folders.Add(up);
            current = up;
        }
        foreach (var folder in folders)
        {
            var candidates = db.GetChildren(folder.Id, 300)
                .Where(c => !c.IsDirectory && c.Size > 0 && c.Size <= MaxReadmeBytes && ReadmeName().IsMatch(c.Name))
                .OrderBy(c => c.Name.StartsWith("read", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            foreach (var c in candidates)
            {
                if (ReadExcerpt(c.Path, e.IsDirectory ? e.Name : Path.GetFileNameWithoutExtension(e.Name)) is { Length: > 0 } text)
                    return new ReadmeExcerpt(c.Path, c.Name, folder.Name, text);
            }
        }
        return null;
    }

    /// <summary>The readme's title line plus the lines that mention the item (or its first lines), at most about 500 characters.</summary>
    public static string? ReadExcerpt(string path, string itemName)
    {
        string raw;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxReadmeBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            raw = Decode(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var lines = raw.Split('\n')
            .Select(l => ControlChars().Replace(l, " ").Trim())
            .Select(l => Regex.Replace(l, @"\s+", " "))
            // Skip ASCII-art and separator lines (.nfo files are full of them).
            .Where(l => l.Length >= 3 && l.Count(char.IsLetterOrDigit) >= Math.Max(3, l.Length * 0.4))
            // A bare link says nothing by itself, nor does the line introducing it ("Mod created by").
            .Where(l => !UrlOnly().IsMatch(l))
            .ToList();
        if (lines.Count == 0) return null;

        var picked = new List<int> { 0 };
        void Take(int i)
        {
            if (i > 0 && i < lines.Count && picked.Count < 6 && !picked.Contains(i)) picked.Add(i);
        }
        // The lines that mention the item...
        if (itemName.Length >= 3)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                if (!lines[i].Contains(itemName, StringComparison.OrdinalIgnoreCase)) continue;
                if (lines[i - 1].EndsWith(':')) Take(i - 1);
                Take(i);
                Take(i + 1);
            }
        }
        // ...or what it is and how it's installed or used.
        if (picked.Count == 1)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                if (!SectionHeading().IsMatch(lines[i])) continue;
                Take(i);
                Take(i + 1);
                Take(i + 2);
            }
        }
        if (picked.Count == 1)
            for (int i = 1; i < lines.Count && picked.Count < 5; i++)
                if (!lines[i].EndsWith(" by", StringComparison.OrdinalIgnoreCase)) Take(i);
        // Don't end on a heading with nothing after it.
        picked = picked.Distinct().Order().ToList();
        while (picked.Count > 1 && lines[picked[^1]].EndsWith(':')) picked.RemoveAt(picked.Count - 1);

        var sb = new StringBuilder();
        foreach (int i in picked.Distinct().Order())
        {
            string line = lines[i];
            if (sb.Length + line.Length > 500) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
            if (!".!?:;".Contains(line[^1])) sb.Append('.');
        }
        return sb.ToString();
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F\r\t]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"^(?:[\w ]{0,20}:\s*)?(?:https?://|www\.)\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlOnly();

    [GeneratedRegex(@"^(?:#+\s*)?(?:install(?:ation|ing)?|how to (?:install|use)|usage|description|about|what(?:'s| is) (?:this|it)|features|requirements)\b.{0,30}:?$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeading();
}
