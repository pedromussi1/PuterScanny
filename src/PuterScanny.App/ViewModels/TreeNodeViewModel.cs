using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PuterScanny.Core.Models;

namespace PuterScanny.App.ViewModels;

/// <summary>A row in the folder tree. Children are loaded from the scan database only when expanded.</summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    public const int PageSize = 500;

    private readonly ResultsContext? _ctx;
    private bool _loaded;

    public TreeNodeViewModel(ResultsContext ctx, FileSystemEntry entry, long parentTotal, string? parentRuleId = null)
    {
        _ctx = ctx;
        Entry = entry;
        Name = entry.Depth == 0 ? entry.Path : entry.Name;
        SizeText = Display.Size(entry.TotalSize);
        Percent = parentTotal > 0 ? Math.Clamp(entry.TotalSize * 100.0 / parentTotal, 0, 100) : 100;
        PercentText = Percent >= 0.1 || entry.TotalSize == 0 ? Percent.ToString("0.#", CultureInfo.CurrentCulture) + "%" : "<0.1%";
        Glyph = PickGlyph(entry);
        DetailText = entry.IsDirectory
            ? $"{Display.Count(entry.FileCount, "file", "files")}, {Display.Count(entry.DirectoryCount, "folder", "folders")}"
              + (entry.Analysis?.ContainedLastUsedUtc is { } inside ? $" · used {Display.Ago(inside)}" : "")
            : entry.Analysis?.LastUsedUtc is { } used ? $"used {Display.Ago(used)}" : $"modified {Display.Date(entry.ModifiedUtc)}";
        HintText = ctx.Hint(entry, parentRuleId);
        if (entry.Analysis is { } rec)
        {
            VerdictTag = Display.VerdictTag(rec.Verdict, rec.Action);
            if (entry.IsDirectory && rec.ReclaimBytes > 0 && rec.Verdict is not (Verdict.SafeToDelete or Verdict.RecommendDelete))
                ReclaimText = $"{Display.Size(rec.ReclaimBytes)} can go";
        }
        if (entry.Analysis is { } an)
        {
            var shown = an.Protection >= an.ContainedProtection ? an.Protection : an.ContainedProtection;
            ProtectionGlyph = Display.ProtectionGlyph(shown);
            ProtectionTip = an.Protection >= an.ContainedProtection || an.Protection > Protection.None
                ? Display.ProtectionTitle(an.Protection)
                : "Holds things that must be kept: " + Display.ProtectionTitle(an.ContainedProtection).ToLowerInvariant();
        }

        if (entry.IsDirectory && entry.FileCount + entry.DirectoryCount > 0)
            Children.Add(CreateLoadingPlaceholder());
        else
            _loaded = true;
    }

    private TreeNodeViewModel(string name, string sizeText, string glyph)
    {
        Name = name;
        SizeText = sizeText;
        PercentText = "";
        Glyph = glyph;
        DetailText = "";
        HintText = "";
        IsPlaceholder = true;
        _loaded = true;
    }

    /// <summary>Null for placeholder rows ("Loading…", "… N more items").</summary>
    public FileSystemEntry? Entry { get; }
    public bool IsPlaceholder { get; }

    public string Name { get; }
    public string SizeText { get; }
    public double Percent { get; }
    public string PercentText { get; }
    public string Glyph { get; }
    public string DetailText { get; }

    public string ProtectionGlyph { get; } = "";
    public string ProtectionTip { get; } = "";
    public bool HasProtection => ProtectionGlyph.Length > 0;

    /// <summary>Who owns it (folders) or what it is (files).</summary>
    public string HintText { get; }

    /// <summary>"Delete", "Review"… or, for folders, how much inside can go.</summary>
    public string VerdictTag { get; } = "";
    public string ReclaimText { get; } = "";
    public bool HasVerdictTag => VerdictTag.Length > 0;
    public bool HasReclaim => ReclaimText.Length > 0;
    public bool ShowBar => !IsPlaceholder;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded) LoadChildren();
    }

    private void LoadChildren()
    {
        _loaded = true;
        Children.Clear();
        if (_ctx is null || Entry is null) return;

        var kids = _ctx.Db.GetChildren(Entry.Id, PageSize);
        foreach (var kid in kids) Children.Add(new TreeNodeViewModel(_ctx, kid, Entry.TotalSize, Entry.Analysis?.LocationRuleId));

        if (kids.Count == PageSize)
        {
            var (count, size) = _ctx.Db.GetChildrenStats(Entry.Id, PageSize);
            if (count > 0)
                Children.Add(new TreeNodeViewModel($"… and {Display.Count(count, "more smaller item", "more smaller items")}", Display.Size(size), ""));
        }
    }

    private static TreeNodeViewModel CreateLoadingPlaceholder() => new("Loading…", "", "");

    private static string PickGlyph(FileSystemEntry e)
    {
        if (e.Flags.HasFlag(EntryFlags.ReparsePointNotFollowed)) return "";   // link
        if (e.Flags.HasFlag(EntryFlags.EnumerationFailed)) return "";         // lock
        if (e.Flags.HasFlag(EntryFlags.CloudPlaceholder)) return "";          // cloud
        if (e.IsDirectory) return e.Depth == 0 ? "" : "";              // drive / folder
        return (e.Analysis?.Category) switch
        {
            "image" => "",
            "video" => "",
            "audio" => "",
            "archive" or "disk-image" => "",
            "executable" or "installer" => "",
            "library" or "driver" => "",
            "document" or "ebook" => "",
            "code" or "script" or "web" => "",
            "config" => "",
            "font" => "",
            "database" or "data" => "",
            "log" or "text" => "",
            "shortcut" => "",
            "certificate" => "",
            _ => "",
        };
    }
}
