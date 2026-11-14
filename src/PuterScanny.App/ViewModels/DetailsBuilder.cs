using PuterScanny.Core.Models;

namespace PuterScanny.App.ViewModels;

/// <summary>Builds the plain-English details panel for one file or folder.</summary>
internal static class DetailsBuilder
{
    public static List<DetailRow> Build(FileSystemEntry e, ResultsContext ctx)
    {
        var rows = new List<DetailRow>();
        void Header(string title) => rows.Add(new DetailRow(title, "", DetailKind.Header));
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new DetailRow(label, value));
        }
        void Warn(string label, string value) => rows.Add(new DetailRow(label, value, DetailKind.Warning));
        void Note(string text) => rows.Add(new DetailRow("", text, DetailKind.Note));

        var a = e.Analysis;
        var kb = ctx.Kb;
        var rule = kb.GetRule(a?.LocationRuleId);
        PeInfo? pe = !e.IsDirectory && a is not null ? ctx.Db.GetPeInfo(e.Id) : null;

        // ---------------- Must it be kept?
        if (a?.Verdict is not null) AddRecommendation(e, a, ctx, Header, Add, Warn, Note);
        if (a is not null) AddSafety(e, a, rule, ctx, Header, Add, Warn, Note);

        // ---------------- How it's been used
        if (a is not null) AddUsage(e, a, ctx, Header, Add, Note);

        // ---------------- What it is
        // ---------------- What its name and dates mean (TorrentZIP dates, ROM and camera names…)
        var good = PuterScanny.Chat.KnownDetailsFinder.For(ctx.Db, kb, e, DateTime.UtcNow);
        if (good.Count > 0)
        {
            Header("Good to know");
            foreach (string g in good) Add("", g);
        }

        Header("What it is");
        Add("Name", e.Depth == 0 ? e.Path : e.Name);
        if (e.IsDirectory)
        {
            Add("Type", e.Depth == 0 ? "Scanned folder / drive" : "Folder");
            if (rule is not null) Add("Known location", $"{rule.Name} — {rule.Description}");
        }
        else
        {
            Add("Type", a?.TypeLabel ?? (e.Extension is { } ext ? $"{ext.TrimStart('.').ToUpperInvariant()} file" : "File"));
            if (a?.Category is { } cat && kb.GetCategory(cat) is { } category) Add("Group", $"{category.Name} — {category.Description}");
            if (a?.ExtensionMismatch == true)
                Warn("Name doesn't match content", $"The name ends in \"{e.Extension}\", but the content is really: {a.TypeLabel}. This is sometimes harmless (an installer's temporary file), but a program disguised as a picture or document is a classic trick. Be careful opening it.");
            if (e.Extension is { } x && ctx.Db.GetExtensionStat(x) is { } stat)
            {
                if (stat.OpensWith is { } app) Add("Opens with", app);
                if (stat.WindowsTypeName is { } wn && !string.Equals(wn, a?.TypeLabel, StringComparison.OrdinalIgnoreCase)) Add("Windows calls it", wn);
            }
            switch (a?.ReadStatus)
            {
                case ReadStatus.AccessDenied:
                    Note("Windows did not allow this file to be opened, so its type is based on its name only.");
                    break;
                case ReadStatus.InUse:
                    Note("This file is locked by a running program (it is in use right now), so its type is based on its name only.");
                    break;
                case ReadStatus.Error:
                    Note("This file could not be read, so its type is based on its name only.");
                    break;
            }
            if (rule is not null) Add("Location", $"{rule.Name} — {rule.Description}");
        }

        // ---------------- Program details
        if (pe is not null)
        {
            Header(pe.Machine.Length == 0 ? "Installer details" : "Program details");
            Add("Description", pe.FileDescription);
            Add("Product", pe.ProductName);
            Add("Company", pe.CompanyName);
            Add("Version", pe.FileVersion ?? pe.ProductVersion);
            if (pe.Machine.Length > 0)
            {
                Add("Kind", ProgramKind(pe));
                Add("Built for", pe.Machine);
            }
            if (pe.OriginalFilename is { } orig && !string.Equals(orig, e.Name, StringComparison.OrdinalIgnoreCase))
                Add("Original file name", orig);
            if (pe.LinkTimeUtc is { } built) Add("Built", Display.Date(built));
            Add("Copyright", pe.LegalCopyright);

            string sig = Display.Signature(pe);
            if (pe.SignatureState is SignatureState.Tampered or SignatureState.Revoked or SignatureState.Untrusted)
                Warn("Digital signature", sig);
            else
                Add("Digital signature", sig);
        }

        // ---------------- Belongs to
        Header("Belongs to");
        if (a?.OwnerAppId is long appId && ctx.Db.GetApp(appId) is { } owner)
        {
            Add("Program", owner.Name);
            Add("Publisher", owner.Publisher);
            Add("Version", owner.Version);
            Add("Installed", owner.InstallDate is { } d ? Display.Date(d) : null);
            Add("Found via", Display.SourceName(owner.Source));
            Add("How we know", owner.Source == AppSource.Portable
                ? (a.OwnerIsExact ? $"This folder holds {owner.Name}.exe, a program that isn't an installer, so the folder is that program."
                                  : $"It is inside the folder of {owner.Name}, a program that runs without being installed.")
                : a.OwnerIsExact
                    ? (e.IsDirectory ? $"This is where {owner.Name} is installed (or keeps its data)." : $"{owner.Name}'s installer registered this exact file.")
                    : $"It is inside {owner.Name}'s folder.");
            if (owner.MeasuredFiles > 0) Add("Program's total here", $"{Display.Size(owner.MeasuredBytes)} in {Display.Count(owner.MeasuredFiles, "file", "files")}");
        }
        else if (rule?.Owner is { } ruleOwner)
        {
            Add("Owner", ruleOwner);
            Add("How we know", $"It is in a known location: {rule.Name}.");
        }
        else if (a is not null)
        {
            Note("No installed program claims this item. It may be your own file, or a leftover from a program that is no longer installed.");
        }
        else
        {
            Note("Ownership was not analysed for this scan.");
        }

        // ---------------- Size & dates
        Header(e.IsDirectory ? "Size & contents" : "Size & dates");
        Add("Location", e.Path);
        if (e.IsDirectory)
        {
            Add("Total size", Display.SizeWithBytes(e.TotalSize));
            Add("Size on disk", Display.SizeWithBytes(e.TotalSizeOnDisk) + " (approximate)");
            Add("Contains", $"{Display.Count(e.FileCount, "file", "files")} in {Display.Count(e.DirectoryCount, "folder", "folders")} (everything inside, at every level)");
        }
        else
        {
            Add("Size", Display.SizeWithBytes(e.Size));
            Add("Size on disk", Display.SizeWithBytes(e.SizeOnDisk) + (e.Flags.HasFlag(EntryFlags.SizeOnDiskEstimated) ? " (estimated)" : ""));
        }
        Add("Created", Display.Date(e.CreatedUtc));
        Add("Last modified", Display.Date(e.ModifiedUtc));
        Add("Last accessed", Display.Date(e.AccessedUtc) + (ctx.Db.GetMeta("usage.last_access_reliable") == "1"
            ? " (file system date: antivirus and search indexing can change it too, so it's only a hint)"
            : " (Windows doesn't keep this date up to date on this PC)"));
        Add("Attributes", Display.Attributes(e.Attributes));

        if (e.ReparseTag != 0)
            Add("Special type", Display.LinkKind(e.ReparseTag) + (e.LinkTarget is { } t ? $" → {t}" : ""));
        if (e.Flags.HasFlag(EntryFlags.ReparsePointNotFollowed) && e.IsDirectory)
            Note("This is a link to another location. Its contents were not counted here, so nothing is counted twice.");
        if (e.Flags.HasFlag(EntryFlags.CloudPlaceholder))
            Note("Online-only: the content lives in the cloud and takes little or no space on this PC. It was not downloaded during the scan.");
        if (e.Flags.HasFlag(EntryFlags.EnumerationFailed))
            Warn("Not fully scanned", "Windows did not allow this folder's contents to be listed (see \"Skipped & issues\"). Its size may be larger than shown.");

        if (e.Flags.HasFlag(EntryFlags.HasAlternateStreams))
        {
            var streams = ctx.Db.GetStreams(e.Id);
            Add("Hidden data streams", string.Join("\n", streams.Select(s => $"{s.Name}  ({Display.Size(s.Size)}){StreamHint(s.Name)}")));
        }

        return rows;
    }

    private static void AddRecommendation(FileSystemEntry e, AnalysisInfo a, ResultsContext ctx,
        Action<string> header, Action<string, string?> add, Action<string, string> warn, Action<string> note)
    {
        var rec = PuterScanny.Analysis.RecommendationExplainer.Explain(ctx.Db, ctx.Kb, e);
        if (rec is null) return;
        header("Recommendation");
        string title = $"{Display.VerdictTitle(rec.Verdict, a.Facts)} ({rec.Confidence}% sure)";
        if (rec.Verdict is Verdict.RecommendDelete or Verdict.SafeToDelete) warn(title, rec.Headline);
        else add(title, rec.Headline);

        foreach (var ev in rec.Evidence)
            add(ev.Weight > 0 ? "For removing" : "For keeping", ev.Text);

        if (Display.ActionText(rec.Action) is { Length: > 0 } action) add("What to do", action);

        if (e.IsDirectory && (a.ReclaimBytes > 0 || a.ReviewBytes > 0 || a.ToolBytes > 0))
        {
            if (a.ReclaimBytes > 0) add("Can be freed inside", Display.SizeWithBytes(a.ReclaimBytes));
            if (a.ReviewBytes > 0) add("Worth reviewing inside", Display.SizeWithBytes(a.ReviewBytes));
            if (a.ToolBytes > 0) add("Freeable with Windows tools", Display.SizeWithBytes(a.ToolBytes));
            if (a.TopReasons is { } top) add("Mostly", top.Replace("; ", "\n"));
        }
    }

    private static void AddSafety(FileSystemEntry e, AnalysisInfo a, PuterScanny.Knowledge.LocationRule? rule, ResultsContext ctx,
        Action<string> header, Action<string, string?> add, Action<string, string> warn, Action<string> note)
    {
        header("Must it be kept?");
        var refs = ctx.Db.GetReferences(e.Id);
        string? ownerName = a.OwnerAppName;

        switch (a.Protection)
        {
            case Protection.Required:
                warn("Must be kept", "Yes. Windows or an active service, task or program needs it. Deleting it could break Windows or a program.");
                break;
            case Protection.InUse:
                warn("In use", "A program was using it during the scan. It can't be removed while that program runs, and removing it would break that program.");
                break;
            case Protection.UninstallOnly when a.OwnerAppId is long pid && ctx.Db.GetApp(pid) is { Source: AppSource.Portable } portable:
                add("Part of a portable program",
                    $"It belongs to {portable.Name}, which runs from its own folder without being installed. Its files must stay together where they are: " +
                    "keep the whole folder, or delete all of it in File Explorer if you no longer want the program.");
                break;
            case Protection.UninstallOnly:
                add("Part of an installed program",
                    $"It belongs to {ownerName ?? "an installed program"}. To get rid of it, uninstall the program (Settings > Apps) instead of deleting files, or the program will break and leave pieces behind.");
                break;
            case Protection.Caution:
                add("Handle with care", "Deleting it has side effects. See the reasons below.");
                break;
            default:
                if (e.IsDirectory && a.ContainedProtection > Protection.None) break;
                note("Nothing on this PC was found to depend on it. (That alone doesn't make it safe to delete: see the recommendation.)");
                break;
        }

        foreach (var r in refs) add(Display.ReferenceKindName(r.Kind), r.Detail);
        if (rule is not null && rule.Protection > Protection.None && a.Protection >= rule.Protection)
            add("Location", $"{rule.Name}: {rule.Description}");

        if (e.IsDirectory && a.ContainedProtection > a.Protection)
        {
            string what = a.ContainedProtection switch
            {
                Protection.Required => "files Windows or programs need",
                Protection.InUse => "files that were in use during the scan",
                Protection.UninstallOnly => "files of installed programs",
                _ => "items that need care",
            };
            warn("Don't delete the whole folder", $"Somewhere inside it are {what}. Even if parts of it can go, the folder as a whole must stay.");
        }
    }

    private static void AddUsage(FileSystemEntry e, AnalysisInfo a, ResultsContext ctx,
        Action<string> header, Action<string, string?> add, Action<string> note)
    {
        header("How it's been used");
        if (a.LastUsedUtc is { } last) add("Last used", Display.DateAgo(last));
        if (a.UseCount > 0) add("Times used", $"{a.UseCount:N0} (the highest count any one record shows)");
        if (a.ActiveTime > TimeSpan.Zero) add("Time in use", Display.Duration(a.ActiveTime));

        foreach (var u in ctx.Db.GetUsage(e.Id))
            add(Display.UsageSourceName(u.Source), u.LastUsedUtc is { } t ? $"{u.Detail}, last on {Display.DateAgo(t)}" : u.Detail);

        if (e.IsDirectory && a.ContainedLastUsedUtc is { } inside)
            add("Anything inside last used", Display.DateAgo(inside));

        if (a.OwnerAppId is long appId && ctx.Db.GetApp(appId) is { } app)
        {
            if (app.LastUsedUtc is { } appUsed)
                add($"{app.Name} last used", Display.DateAgo(appUsed) + (app.ActiveTime > TimeSpan.Zero ? $", {Display.Duration(app.ActiveTime)} in use in total" : ""));
            else if (a.OwnerIsExact || !e.IsDirectory)
                add($"{app.Name} last used", "No record of it being used was found.");
        }

        bool anything = a.LastUsedUtc is not null || (e.IsDirectory && a.ContainedLastUsedUtc is not null);
        if (!anything)
        {
            note(e.IsDirectory
                ? "No record of anything inside being opened was found in Windows' usage history."
                : "No record of it being opened was found in Windows' usage history (program launches, recent items, apps' recent lists, Office and Steam). Records that need administrator rights weren't checked.");
        }
    }

    private static string ProgramKind(PeInfo pe)
    {
        string kind = pe.IsDriver ? "Driver (runs inside Windows itself)"
            : pe.IsDll ? "Program component (used by other programs)"
            : pe.Subsystem;
        return pe.IsDotNet ? kind + ", built with .NET" : kind;
    }

    private static string StreamHint(string name) => name switch
    {
        "Zone.Identifier" => " — marks the file as downloaded from the internet",
        "SmartScreen" => " — Windows SmartScreen check result",
        "com.dropbox.attrs" or "com.dropbox.attributes" => " — Dropbox sync data",
        _ => "",
    };
}
