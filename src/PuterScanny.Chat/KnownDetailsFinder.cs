using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Chat;

/// <summary>
/// What an item's name and dates mean by known conventions (known-details.json), and for a folder, what the names
/// and dates of the files inside do ("12 files here carry TorrentZIP's fixed date…"). Used by the Ask tab and the
/// details panel.
/// </summary>
public static class KnownDetailsFinder
{
    public static IReadOnlyList<string> For(ScanDatabase db, KnowledgeBase kb, FileSystemEntry e, DateTime nowUtc, IEnumerable<string>? exampleNames = null)
    {
        var details = kb.Details;
        if (details.Count == 0) return [];
        var list = new List<string>();
        HashSet<string>? siblings = null;
        bool SiblingExists(string name)
        {
            siblings ??= e.ParentId is long pid
                ? db.GetChildren(pid, 5000).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            return !string.Equals(name, e.Name, StringComparison.OrdinalIgnoreCase) && siblings.Contains(name);
        }

        if (e.Depth > 0) list.AddRange(details.ForName(e.Name, e.IsDirectory, e.Size, SiblingExists).Select(d => d.Text));
        if (!e.IsDirectory && details.ForDate(e.ModifiedUtc, nowUtc) is { } date) list.Add(date.Text);
        if (e.IsDirectory)
        {
            var names = (exampleNames ?? []).Select(n => (n, 0L))
                .Concat(db.GetChildren(e.Id, 60).Where(c => !c.IsDirectory).Select(c => (c.Name, c.Size)));
            list.AddRange(details.ForFolderContents(names).Select(d => d.Text));
            foreach (var (id, ticks) in details.ExactPlaceholderTicks())
            {
                long n = db.CountFilesModifiedAt(e.Path, ticks);
                if (n > 0 && details.FolderTextForDate(id, ChatText.Count(n, "file", "files")) is { } text) list.Add(ChatText.Sentence(text));
            }
        }
        return list.Distinct().ToList();
    }
}
