using System.Diagnostics;
using PuterScanny.Core.Abstractions;
using PuterScanny.Core.Models;

namespace PuterScanny.Tests;

/// <summary>Collects everything a scan emits, in memory.</summary>
internal sealed class ListSink : IScanSink
{
    public ScanSummary? Initial { get; private set; }
    public ScanSummary? Final { get; private set; }
    public List<FileSystemEntry> Entries { get; } = [];
    public List<AlternateStream> Streams { get; } = [];
    public List<ScanIssue> Issues { get; } = [];
    public Dictionary<long, DirectoryTotals> Totals { get; } = [];

    public void Begin(ScanSummary initial) => Initial = initial;
    public void AddEntry(FileSystemEntry entry) => Entries.Add(entry);
    public void AddStream(AlternateStream stream) => Streams.Add(stream);
    public void AddIssue(ScanIssue issue) => Issues.Add(issue);

    public void SetDirectoryTotals(IEnumerable<DirectoryTotals> totals)
    {
        foreach (var t in totals) Totals[t.Id] = t;
    }

    public void Complete(ScanSummary final) => Final = final;

    public FileSystemEntry Find(string name) => Entries.Single(e => e.Name == name);
}

/// <summary>A throwaway folder tree under %TEMP% that deletes itself.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "PuterScannyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relativePath, int sizeBytes)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    public string Dir(string relativePath) => Directory.CreateDirectory(Path.Combine(Root, relativePath)).FullName;

    /// <summary>Creates a directory junction (no admin rights needed).</summary>
    public void Junction(string linkRelative, string targetRelative)
    {
        string link = Path.Combine(Root, linkRelative);
        string target = Path.Combine(Root, targetRelative);
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("mklink /J failed: " + p.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so deleting never follows them.
            foreach (var d in new DirectoryInfo(Root).EnumerateDirectories("*", SearchOption.AllDirectories)
                         .Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
                d.Delete();
            Directory.Delete(@"\\?\" + Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; %TEMP% is cleaned up eventually anyway.
        }
    }
}
