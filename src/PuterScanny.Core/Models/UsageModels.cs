namespace PuterScanny.Core.Models;

/// <summary>Where a piece of usage history came from.</summary>
public enum UsageSource
{
    /// <summary>Programs you started from the Start menu, taskbar or File Explorer (Windows' UserAssist record).</summary>
    ProgramLaunches = 0,

    /// <summary>Windows' "Recent items" list.</summary>
    RecentItems = 1,

    /// <summary>The recent/frequent lists apps show when you right-click them on the taskbar.</summary>
    JumpLists = 2,

    /// <summary>Office's own recent documents list.</summary>
    OfficeRecent = 3,

    /// <summary>Steam's record of when each game was last played and for how long.</summary>
    Steam = 4,

    /// <summary>The file system's "last accessed" date (only used when Windows keeps it up to date).</summary>
    LastAccess = 5,

    // Need administrator rights (Deep scan):
    Prefetch = 10,
    BackgroundActivity = 11,
    Amcache = 12,
}

/// <summary>One record that something was used.</summary>
public sealed record UsageEvent(UsageSource Source, DateTime? LastUsedUtc, int? Count, TimeSpan? ActiveTime, string Detail);

/// <summary>Whether a usage source could be read on this PC, so the report can say what was (and wasn't) checked.</summary>
public sealed record UsageSourceStatus(UsageSource Source, bool Available, int Items, string Note);

/// <summary>Combined usage facts for one item (or, for folders and programs, anything inside).</summary>
public sealed record UsageSummary(DateTime? LastUsedUtc, int UseCount, TimeSpan ActiveTime, UsageSource? BestSource);
