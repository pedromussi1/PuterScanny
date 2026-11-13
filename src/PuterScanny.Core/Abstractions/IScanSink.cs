using PuterScanny.Core.Models;

namespace PuterScanny.Core.Abstractions;

/// <summary>
/// Receives scan output as it is produced. Calls arrive on a single thread, in this order:
/// <see cref="Begin"/>, then any mix of entries/streams/issues, then <see cref="SetDirectoryTotals"/>, then <see cref="Complete"/>.
/// </summary>
public interface IScanSink
{
    void Begin(ScanSummary initial);
    void AddEntry(FileSystemEntry entry);
    void AddStream(AlternateStream stream);
    void AddIssue(ScanIssue issue);
    void SetDirectoryTotals(IEnumerable<DirectoryTotals> totals);
    void Complete(ScanSummary final);
}
