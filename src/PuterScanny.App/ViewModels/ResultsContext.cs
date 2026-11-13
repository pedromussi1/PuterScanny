using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.App.ViewModels;

/// <summary>What the results views need: the scan database plus the knowledge base to name things.</summary>
public sealed class ResultsContext(ScanDatabase db, KnowledgeBase kb)
{
    public ScanDatabase Db { get; } = db;
    public KnowledgeBase Kb { get; } = kb;

    /// <summary>A short hint shown next to a tree row: who owns it, or what it is.</summary>
    /// <param name="parentRuleId">The parent folder's location rule; a different rule here means this item itself is a known location.</param>
    public string Hint(FileSystemEntry e, string? parentRuleId)
    {
        var a = e.Analysis;
        if (a is null) return "";
        var rule = Kb.GetRule(a.LocationRuleId);
        if (e.IsDirectory)
        {
            if (a.OwnerAppName is { } owner && a.OwnerIsExact) return owner;
            if (rule is not null && !string.Equals(a.LocationRuleId, parentRuleId, StringComparison.Ordinal)) return rule.Name;
            return a.OwnerAppName ?? "";
        }
        string label = a.TypeLabel ?? "";
        if (a.ExtensionMismatch) label = "⚠ " + label;
        return label;
    }
}
