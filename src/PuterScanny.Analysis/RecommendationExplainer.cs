using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Signals.Usage;
using PuterScanny.Storage;

namespace PuterScanny.Analysis;

/// <summary>Rebuilds the full reasoning behind a stored verdict, for the details panel.</summary>
public static class RecommendationExplainer
{
    public static Recommendation? Explain(ScanDatabase db, KnowledgeBase kb, FileSystemEntry e)
    {
        var a = e.Analysis;
        if (a?.Verdict is not { } storedVerdict) return null;

        var parent = e.ParentId is long p ? db.GetEntry(p) : null;
        var rule = kb.GetRule(a.LocationRuleId);
        bool matchedHere = parent is null ? rule is not null : !string.Equals(parent.Analysis?.LocationRuleId, a.LocationRuleId, StringComparison.Ordinal);
        var owner = a.OwnerAppId is long id ? db.GetApp(id) : null;
        var dup = db.GetDuplicateInfo(e.Id);

        var facts = new EntryFacts
        {
            Entry = e,
            Rule = rule,
            RuleMatchedHere = matchedHere,
            Owner = owner,
            Flags = a.Facts,
            DuplicateKeeper = dup?.KeeperPath,
            DuplicateCopies = dup?.Copies ?? 0,
            ShortcutTarget = a.Facts.HasFlag(FactFlags.BrokenShortcut) ? ShellLink.ReadTarget(e.Path) : null,
            ContainedLastModifiedUtc = a.ContainedLastModifiedUtc,
            OwnerUninstall = owner is { UninstallConfidence: > 0 } ? new UninstallSuggestion(owner.Id, owner.UninstallConfidence, owner.UninstallReason ?? "") : null,
            NowUtc = db.GetSummary()?.CompletedUtc ?? DateTime.UtcNow,
        };
        var own = Recommender.Evaluate(facts);

        // Folders can be re-judged from their contents; the stored verdict is the final word.
        return own with
        {
            Verdict = storedVerdict,
            Confidence = a.Confidence,
            Action = a.Action,
            Headline = a.Headline ?? own.Headline,
        };
    }
}
