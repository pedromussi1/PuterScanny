using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;

namespace PuterScanny.Analysis;

/// <summary>Everything the recommendation for one item depends on.</summary>
public sealed record EntryFacts
{
    public required FileSystemEntry Entry { get; init; }

    /// <summary>The known location (rule) this item is in, if any.</summary>
    public LocationRule? Rule { get; init; }

    /// <summary>The rule names this very item (not just one of its parent folders).</summary>
    public bool RuleMatchedHere { get; init; }

    public InstalledApp? Owner { get; init; }
    public FactFlags Flags { get; init; }

    /// <summary>For duplicates: the copy that should be kept.</summary>
    public string? DuplicateKeeper { get; init; }
    public int DuplicateCopies { get; init; }

    /// <summary>For broken shortcuts: where the shortcut points.</summary>
    public string? ShortcutTarget { get; init; }

    /// <summary>For folders: the newest change to anything inside.</summary>
    public DateTime? ContainedLastModifiedUtc { get; init; }

    /// <summary>The program owning this item is suggested for uninstalling.</summary>
    public UninstallSuggestion? OwnerUninstall { get; init; }

    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    /// <summary>For items inside a mod download: the mod's name.</summary>
    public string? ModDownloadName { get; init; }
}

/// <summary>
/// Turns the facts about one file or folder into a keep-or-delete verdict with plain-English reasons.
/// Order of precedence (strongest first):
/// 1. Hard vetoes: anything Windows, a running program or an installed program needs is kept, whatever else is true.
/// 2. Online-only cloud files take no space and are kept.
/// 3. Clear findings: broken shortcuts, unfinished downloads, duplicates, leftovers, empty folders.
/// 4. Knowledge-base advice for the location (temp, caches, crash dumps, developer caches…), with a minimum age.
/// 5. File-type hints (temporary, cache, crash dump, old logs and backups).
/// Recent use always argues for keeping, and personal files are never more than "Review".
/// Pure and deterministic: the same facts always give the same answer, so the app can re-explain any verdict.
/// </summary>
public static class Recommender
{
    private static readonly HashSet<string> PersonalCategories = new(StringComparer.Ordinal)
    {
        "document", "image", "video", "audio", "ebook", "email", "design", "3d", "code", "text", "data", "database",
        "certificate", "game", "subtitle", "font", "virtual-machine",
    };

    private static readonly HashSet<string> PersonalLocationCategories = new(StringComparer.Ordinal) { "user-files", "cloud", "game" };

    private static readonly HashSet<string> NonPersonalAreas = new(StringComparer.Ordinal)
    {
        "windows", "drivers", "updates", "program", "program-data", "installer-cache", "user-settings", "browser-data", "cache", "temp",
        "logs", "crash-dumps", "dev-cache", "backup", "recycle-bin", "virtual-machine", "system-memory",
    };

    public static Recommendation Evaluate(EntryFacts f)
    {
        var e = f.Entry;
        var a = e.Analysis ?? new AnalysisInfo();
        // ---------------------------------------------------------------- 1. hard vetoes
        switch (a.Protection)
        {
            case Protection.Required:
                return new(Verdict.Required, 99, CleanupAction.None, "Windows or an active program needs it",
                    [new(-100, "Windows, a service, a driver, a scheduled task or a registration depends on it (see \"Must it be kept?\").")]);
            case Protection.InUse:
                return new(Verdict.Keep, 95, CleanupAction.None, "In use by a running program",
                    [new(-100, "A running program was using it during the scan.")]);
            case Protection.UninstallOnly when f.Owner?.Source == AppSource.Portable:
                return Portable(f, e, a);
            case Protection.UninstallOnly:
            {
                string app = f.Owner?.Name ?? a.OwnerAppName ?? "an installed program";
                if (f.OwnerUninstall is { } u && e.IsDirectory && a.OwnerIsExact)
                    return new(Verdict.Review, u.Confidence, CleanupAction.Uninstall, $"Consider uninstalling {app}",
                        [new(-50, $"It's {app}'s own folder, so remove it by uninstalling (Settings > Apps), never by deleting files."), new(40, u.Reason)]);
                return new(Verdict.Keep, 90, CleanupAction.None, $"Part of {app}",
                    [new(-80, $"It belongs to {app}. To remove it, uninstall the program instead of deleting files, or the program will break.")]);
            }
        }

        if (e.Flags.HasFlag(EntryFlags.CloudPlaceholder))
            return new(Verdict.Keep, 95, CleanupAction.None, "Online-only: uses no space here",
                [new(-90, "It's stored in the cloud and takes almost no space on this PC. Deleting it here would delete it from the cloud too.")]);
        if (e.Flags.HasFlag(EntryFlags.ReparsePointNotFollowed))
            return new(Verdict.Keep, 80, CleanupAction.None, "A link to another place",
                [new(-60, "It's a link to another folder; it takes no space itself, and removing it could break whatever relies on it.")]);

        bool personalArea = f.Rule is null ? !InAppArea(e.Path) : PersonalLocationCategories.Contains(f.Rule.Category) && !NonPersonalAreas.Contains(f.Rule.Category);
        bool personalContent = !e.IsDirectory && personalArea && a.Category is { } cat && PersonalCategories.Contains(cat);
        var age = AgeInDays(f);
        int usedDaysAgo = a.LastUsedUtc is { } lu ? (int)(f.NowUtc - lu).TotalDays : int.MaxValue;
        if (e.IsDirectory && a.ContainedLastUsedUtc is { } clu) usedDaysAgo = Math.Min(usedDaysAgo, (int)(f.NowUtc - clu).TotalDays);

        Recommendation? decided = null;

        // ---------------------------------------------------------------- 3. clear findings
        if (f.Flags.HasFlag(FactFlags.BrokenShortcut))
        {
            decided = new(Verdict.RecommendDelete, 90, CleanupAction.Delete, "Broken shortcut",
                [new(80, $"It points to \"{f.ShortcutTarget}\", which no longer exists.")]);
        }
        else if (f.Flags.HasFlag(FactFlags.PartialDownload))
        {
            decided = age >= 2
                ? new(Verdict.RecommendDelete, 90, CleanupAction.Delete, "Unfinished download",
                    [new(80, $"A download that never finished (last changed {Days(age)} ago). Browsers don't resume downloads this old.")])
                : new(Verdict.Keep, 70, CleanupAction.None, "Download in progress", [new(-50, "A download that may still be running.")]);
        }
        else if (f.Flags.HasFlag(FactFlags.Duplicate) && !f.Flags.HasFlag(FactFlags.DuplicateKeeper) && f.DuplicateKeeper is { } keeper)
        {
            string text = $"Identical to \"{keeper}\" ({f.DuplicateCopies} copies in total, checked byte for byte).";
            decided = personalContent || personalArea
                ? new(Verdict.Review, 80, CleanupAction.KeepOneCopy, "Duplicate copy of your file", [new(50, text), new(-10, "It's one of your own files, so you decide which copy to keep.")])
                : new(Verdict.SafeToDelete, 85, CleanupAction.KeepOneCopy, "Duplicate copy", [new(60, text)]);
        }
        else if (f.Flags.HasFlag(FactFlags.Leftover))
        {
            int quiet = Math.Min(age, usedDaysAgo);
            decided = quiet >= 365
                ? new(Verdict.Review, 70, CleanupAction.Delete, "Leftover from a removed program",
                    [new(50, "No installed program matches this folder's name, so it's probably left over from one that was uninstalled."),
                     new(15, $"Nothing inside has changed for {Days(quiet)}."),
                     new(-15, "It may still hold settings or saves you care about; have a look first.")])
                : new(Verdict.Review, 55, CleanupAction.Delete, "Possibly left over from a removed program",
                    [new(40, "No installed program matches this folder's name."),
                     new(-25, $"But something inside changed {Days(quiet)} ago, so it may still be in use (for example by a portable program).")]);
        }
        else if (f.Flags.HasFlag(FactFlags.ModDownload) && ModDownloads.TryParse(e.Name, e.IsDirectory, out var mod))
        {
            decided = new(Verdict.Review, 65, CleanupAction.Delete, "Mod download (Nexus Mods)",
                [new(40, $"Named the way Nexus Mods names its downloads: \"{mod.Name}\", mod #{mod.ModId}, version {mod.Version}, uploaded {mod.UploadedUtc:d MMM yyyy}. " +
                         (e.IsDirectory ? "This looks like the downloaded mod, unpacked. " : "") +
                         "Once a mod is installed into its game, the downloaded copy is only a spare, and it can be downloaded again from Nexus Mods."),
                 new(-20, "Keep it if you haven't installed the mod yet, or if a mod manager (Vortex, Mod Organizer) uses it to reinstall the mod.")]);
        }
        else if (f.Flags.HasFlag(FactFlags.InsideModDownload))
        {
            string modName = f.ModDownloadName ?? "a mod download";
            decided = new(Verdict.Review, 60, CleanupAction.None, $"Part of a mod download ({modName})",
                [new(20, $"It's inside the download of the mod \"{modName}\" from Nexus Mods, so it belongs to the mod rather than being your own file. " +
                         "Decide about the mod download as a whole.")]);
        }
        else if (f.Flags.HasFlag(FactFlags.EmptyFolder))
        {
            decided = InAppArea(e.Path)
                ? new(Verdict.Keep, 60, CleanupAction.None, "Empty folder (a program may expect it)",
                    [new(-20, "It's empty, but it sits among program data where apps sometimes expect their folders to exist. It uses no space anyway.")])
                : new(Verdict.SafeToDelete, 75, CleanupAction.Delete, "Empty folder",
                    [new(40, "It holds no files at all (it takes no real space, but it's clutter).")]);
        }
        else if (f.Flags.HasFlag(FactFlags.InstallerInDownloads))
        {
            int quiet = Math.Min(age, usedDaysAgo);
            if (quiet >= 30)
            {
                decided = f.Flags.HasFlag(FactFlags.MatchesInstalledProgram)
                    ? new(Verdict.RecommendDelete, 85, CleanupAction.Delete, "Installer for a program you already have",
                        [new(60, "A setup file in Downloads, and the program it installs is installed."), new(20, $"Not used for {Days(quiet)}; it can be downloaded again if ever needed.")])
                    : new(Verdict.SafeToDelete, 75, CleanupAction.Delete, "Old installer in Downloads",
                        [new(45, "A setup file in Downloads."), new(20, $"Not used for {Days(quiet)}; installers can be downloaded again if needed.")]);
            }
        }

        // Big downloaded archives and disk images (ZIP, ISO…) that sit unused for months are usually already extracted or installed.
        if (decided is null && !e.IsDirectory && a.Category is "archive" or "disk-image"
            && (f.Rule?.Id == "user.downloads" || e.Path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase)))
        {
            int quiet = Math.Min(age, usedDaysAgo);
            if (quiet >= 90)
                decided = new(Verdict.Review, 60, CleanupAction.Delete, "Old download you may not need any more",
                    [new(35, a.Category == "archive"
                        ? $"A downloaded archive that hasn't been changed or opened for {Days(quiet)}. Archives are usually extracted soon after downloading."
                        : $"A downloaded disk image that hasn't been changed or opened for {Days(quiet)}. Disk images are usually used soon after downloading."),
                     new(-15, "Check you've kept what was inside before deleting it; it can usually be downloaded again.")]);
        }

        // ---------------------------------------------------------------- 4. location advice
        if (decided is null && f.Rule?.Advice is { } advice && (!f.RuleMatchedHere || advice.DeleteFolder || !e.IsDirectory))
        {
            decided = FromAdvice(f, advice, age, usedDaysAgo);
        }
        else if (decided is null && f.Rule?.Advice is { Verdict: "recommend" or "safe" or "review" } container && f.RuleMatchedHere && e.IsDirectory)
        {
            decided = new(Verdict.Keep, 85, CleanupAction.None, $"{f.Rule.Name} (the folder itself stays)",
                [new(-40, $"The folder itself is needed; its contents are what can be cleaned. {container.Why}")]);
        }

        // ---------------------------------------------------------------- 5. file-type hints
        if (decided is null && !e.IsDirectory && !personalContent)
        {
            decided = a.Category switch
            {
                "crash-dump" when age >= 14 => new(Verdict.RecommendDelete, 85, CleanupAction.Delete, "Old crash dump",
                    [new(70, $"A memory snapshot from a crash {Days(age)} ago; only useful for diagnosing that crash.")]),
                "temporary" when age >= 7 => new(Verdict.SafeToDelete, 75, CleanupAction.Delete, "Old temporary file",
                    [new(50, $"A temporary file, last changed {Days(age)} ago. Programs normally clean up their own temporary files; this one was left behind.")]),
                "cache" when age >= 7 => new(Verdict.SafeToDelete, 70, CleanupAction.Delete, "Cache file",
                    [new(45, "A cache file; the program that made it can rebuild it.")]),
                "log" when age >= 90 => new(Verdict.SafeToDelete, 70, CleanupAction.Delete, "Old log file",
                    [new(40, $"A log file not changed for {Days(age)}; old logs only help troubleshoot old problems.")]),
                "backup" when age >= 180 && usedDaysAgo >= 180 => new(Verdict.Review, 60, CleanupAction.Delete, "Old backup copy",
                    [new(30, $"A backup copy not changed for {Days(age)}. Check you don't need it before deleting.")]),
                _ => null,
            };
        }

        if (decided is null && !e.IsDirectory && a.ExtensionMismatch && a.FormatId is { } fmt && fmt.StartsWith("pe-", StringComparison.Ordinal) && a.Category is not "temporary")
        {
            decided = new(Verdict.Review, 70, CleanupAction.None, "A program disguised as another kind of file",
                [new(20, $"Its name says it's a \"{e.Extension}\" file, but it's really a Windows program. That's a common trick in malware; check where it came from before opening it.")]);
        }

        // ---------------------------------------------------------------- 6. nothing decisive: keep
        decided ??= personalContent || (personalArea && e.IsDirectory)
            ? new(Verdict.KeepPersonal, 90, CleanupAction.None, "Your own " + (e.IsDirectory ? "folder" : "file"),
                [new(-70, "It looks like your own " + (e.IsDirectory ? "folder" : "file") + ". PuterScanny never suggests deleting your files on its own.")])
            : new(Verdict.Keep, 60, CleanupAction.None, "No reason found to remove it",
                [new(-20, "Nothing found says what it is for or that it's safe to remove, so it's left alone. This isn't a sign it's needed, only that PuterScanny can't tell.")])
              { NoEvidence = true };

        return Adjust(decided, f, a, personalContent, usedDaysAgo);
    }

    /// <summary>A program that runs from its own folder without being installed: the folder is kept or deleted as a whole.</summary>
    private static Recommendation Portable(EntryFacts f, FileSystemEntry e, AnalysisInfo a)
    {
        string app = f.Owner!.Name;
        string lastRun = f.Owner.LastUsedUtc is { } used ? $"{app} last ran {Days((int)(f.NowUtc - used).TotalDays)} ago." : "";
        if (e.IsDirectory && a.OwnerIsExact)
        {
            var ev = new List<Evidence>
            {
                new(-70, $"{app} runs straight from this folder without being installed, so it isn't listed in Settings > Apps. " +
                         "Everything in here belongs together: keep the folder whole, or delete all of it if you no longer use the program."),
            };
            if (lastRun.Length > 0) ev.Add(new(-10, lastRun));
            return new(Verdict.Keep, 90, CleanupAction.None, $"Portable program: {app}", ev);
        }
        return new(Verdict.Keep, 90, CleanupAction.None, $"Part of {app} (portable program)",
            [new(-80, $"It belongs to {app}, a program that runs from its own folder. Programs like this need their files (data, disc images, libraries) " +
                      "right where they are, so removing pieces would break it. Keep the folder whole, or delete all of it.")]);
    }

    private static Recommendation FromAdvice(EntryFacts f, LocationAdvice advice, int age, int usedDaysAgo)
    {
        var rule = f.Rule!;
        switch (advice.Verdict)
        {
            case "keep":
                return new(Verdict.Keep, 85, CleanupAction.None, $"Keep: {rule.Name}", [new(-60, advice.Why)]);
            case "inapp":
                return new(Verdict.Keep, 85, CleanupAction.ClearInApp, $"Manage it from {rule.Owner ?? "its app"}",
                    [new(-50, advice.Why)]);
            case "tool":
                return new(Verdict.Review, 90, CleanupAction.UseWindowsTool, $"Free this space with: {advice.Tool}",
                    [new(60, advice.Why), new(-20, $"Use {advice.Tool} rather than deleting it by hand, so Windows stays consistent.")]);
        }

        int quiet = Math.Min(age, usedDaysAgo);
        if (quiet < advice.MinAgeDays)
        {
            return new(Verdict.Keep, 70, CleanupAction.None, $"{rule.Name}: recently in use",
                [new(30, advice.Why), new(-60, $"But it changed or was used {Days(quiet)} ago, so it may still be needed. It qualifies once it's {advice.MinAgeDays} days old.")]);
        }

        var ev = new List<Evidence> { new(60, $"{rule.Name}: {advice.Why}") };
        if (advice.MinAgeDays > 0) ev.Add(new(15, $"Not changed or used for {Days(quiet)}."));
        return advice.Verdict switch
        {
            "recommend" => new(Verdict.RecommendDelete, 90, CleanupAction.Delete, rule.Name, ev),
            "safe" => new(Verdict.SafeToDelete, 85, CleanupAction.Delete, rule.Name, ev),
            _ => new(Verdict.Review, 65, CleanupAction.Delete, rule.Name, ev),
        };
    }

    /// <summary>Recent use argues for keeping; personal files and "caution" items never go beyond Review.</summary>
    private static Recommendation Adjust(Recommendation r, EntryFacts f, AnalysisInfo a, bool personalContent, int usedDaysAgo)
    {
        var ev = r.Evidence.ToList();
        var verdict = r.Verdict;
        int confidence = r.Confidence;

        bool deletable = verdict is Verdict.SafeToDelete or Verdict.RecommendDelete or Verdict.Review;
        bool fromAdvice = f.Rule?.Advice is not null && r.Headline.StartsWith(f.Rule.Name, StringComparison.Ordinal);
        if (deletable && !fromAdvice && usedDaysAgo < 30 && r.Action != CleanupAction.Uninstall)
        {
            ev.Add(new(-40, $"You used it {Days(usedDaysAgo)} ago."));
            verdict = verdict == Verdict.RecommendDelete ? Verdict.SafeToDelete : Verdict.Review;
            confidence -= 15;
        }

        // A folder can only go as a whole if nothing inside must stay.
        if (f.Entry.IsDirectory && verdict is Verdict.SafeToDelete or Verdict.RecommendDelete or Verdict.Review
            && r.Action is CleanupAction.Delete or CleanupAction.KeepOneCopy)
        {
            if (a.ContainedProtection >= Protection.UninstallOnly)
            {
                ev.Add(new(-80, "Something inside must be kept (in use, or needed by Windows or a program), so the folder can't be deleted as a whole."));
                verdict = Verdict.Keep;
                confidence = 85;
            }
            else if (a.ContainedProtection == Protection.Caution && verdict > Verdict.Review)
            {
                ev.Add(new(-30, "Something inside needs care, so the folder is only offered for review."));
                verdict = Verdict.Review;
            }
        }

        if (personalContent && verdict > Verdict.Review)
        {
            ev.Add(new(-30, "It's one of your own files, so at most it's offered for review."));
            verdict = Verdict.Review;
        }
        if (a.Protection == Protection.Caution && verdict > Verdict.Review)
        {
            ev.Add(new(-30, "Deleting it has side effects (see \"Must it be kept?\"), so it's only offered for review."));
            verdict = Verdict.Review;
        }

        if (verdict == r.Verdict && ev.Count == r.Evidence.Count) return r;
        var action = verdict is Verdict.Keep or Verdict.KeepPersonal or Verdict.Required ? CleanupAction.None : r.Action;
        string headline = verdict == Verdict.Keep && r.Verdict != Verdict.Keep ? "Parts of it can be cleaned, but it must stay" : r.Headline;
        return r with { Verdict = verdict, Confidence = Math.Clamp(confidence, 40, 99), Evidence = ev, Action = action, Headline = headline };
    }

    /// <summary>Days since this item (or, for folders, anything inside) last changed.</summary>
    private static int AgeInDays(EntryFacts f)
    {
        var e = f.Entry;
        DateTime newest = e.ModifiedUtc;
        if (e.CreatedUtc > newest && e.CreatedUtc <= f.NowUtc) newest = e.CreatedUtc;
        if (e.IsDirectory && f.ContainedLastModifiedUtc is { } inside && inside > newest) newest = inside;
        if (newest == default) return int.MaxValue;
        return Math.Max(0, (int)(f.NowUtc - newest).TotalDays);
    }

    /// <summary>Program, program-data and Windows areas, where unknown items are never treated as "yours".</summary>
    public static bool InAppArea(string path)
    {
        return path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(@"\AppData", StringComparison.OrdinalIgnoreCase)
            || Starts(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            || Starts(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || Starts(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
            || Starts(path, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    private static bool Starts(string path, string root) =>
        root.Length > 0 && (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));

    public static string Days(int days) => days switch
    {
        int.MaxValue => "a long time",
        0 => "less than a day",
        1 => "1 day",
        < 60 => $"{days} days",
        < 730 => $"{days / 30} months",
        _ => $"{days / 365.0:0.#} years",
    };

    /// <summary>Verdict for a folder from what's inside it, when the folder itself had no decisive finding.</summary>
    public static Recommendation ForFolderContents(Recommendation own, Verdict? weakestDeletableInside, bool everythingInsideDeletable,
        long reclaimable, long total, Protection containedProtection, string? topReason)
    {
        if (own.Verdict is not (Verdict.Keep or Verdict.KeepPersonal) || own.Action != CleanupAction.None) return own;

        // Strong "keep" verdicts (your own folders, known locations) stay as they are; they only learn what inside can be freed.
        if (own.Confidence >= 85)
            return reclaimable > 0
                ? own with { Headline = $"{own.Headline} · {ByteSize.Format(reclaimable)} inside can be cleaned{(topReason is null ? "" : $" (mostly: {topReason})")}" }
                : own;

        if (containedProtection >= Protection.UninstallOnly)
            return reclaimable > 0
                ? own with { Headline = $"{ByteSize.Format(reclaimable)} inside can be cleaned{(topReason is null ? "" : $" (mostly: {topReason})")}" }
                : own;

        if (everythingInsideDeletable && total > 0 && weakestDeletableInside is { } v)
        {
            return new(v, 80, CleanupAction.Delete, $"Everything inside can go{(topReason is null ? "" : $" (mostly: {topReason})")}",
                [new(60, $"All {ByteSize.Format(total)} inside it is safe to delete.")]);
        }

        return reclaimable > 0
            ? own with { Headline = $"{ByteSize.Format(reclaimable)} inside can be cleaned{(topReason is null ? "" : $" (mostly: {topReason})")}" }
            : own;
    }
}
