using PuterScanny.Core.Models;

namespace PuterScanny.AI;

/// <summary>Which items the local AI is for: the ones PuterScanny's own rules couldn't put a name to.</summary>
public static class AiTargets
{
    /// <summary>
    /// True when no installed program claims the item, it isn't in a known location, and (for files) its type
    /// isn't a known one. Same rule as <c>ScanDatabase.GetUnidentifiedItems</c>.
    /// </summary>
    public static bool NeedsExplanation(FileSystemEntry e)
    {
        var a = e.Analysis;
        if (a is null || e.Depth == 0) return false;
        if (a.OwnerAppId is not null || a.LocationRuleId is not null) return false;
        return e.IsDirectory || a.Category is null or "other";
    }
}
