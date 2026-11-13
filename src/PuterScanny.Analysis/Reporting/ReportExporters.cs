using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Analysis.Reporting;

/// <summary>Writes a scan out as a self-contained HTML report, a JSON report, or a CSV of every single item.</summary>
public static class ReportExporters
{
    // ------------------------------------------------------------------ JSON

    public static void WriteJson(ReportData report, string path)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, options), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ CSV

    private static readonly string[] CsvHeader =
    [
        "Path", "Type", "Size (bytes)", "Size on disk (bytes)", "Files inside", "Created", "Modified", "Last used", "Times used",
        "What it is", "Kind", "Belongs to", "Known location", "Must keep", "Recommendation", "Confidence %", "What to do", "Why",
        "Can be freed inside (bytes)", "Worth reviewing inside (bytes)",
    ];

    /// <summary>Every file and folder of the scan, one row each (opens in Excel). Returns the number of rows written.</summary>
    public static long WriteCsv(ScanDatabase db, KnowledgeBase kb, string path, IProgress<long>? rowsWritten = null, CancellationToken ct = default)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true)); // BOM so Excel reads accents correctly
        writer.WriteLine(string.Join(",", CsvHeader.Select(Csv)));
        long rows = 0;
        using var read = db.OpenReadConnection();
        foreach (var e in ScanDatabase.ReadAllEntries(read))
        {
            var a = e.Analysis;
            var rule = kb.GetRule(a?.LocationRuleId);
            string[] fields =
            [
                e.Path,
                e.IsDirectory ? "Folder" : "File",
                e.TotalSize.ToString(CultureInfo.InvariantCulture),
                e.TotalSizeOnDisk.ToString(CultureInfo.InvariantCulture),
                e.IsDirectory ? e.FileCount.ToString(CultureInfo.InvariantCulture) : "",
                Date(e.CreatedUtc),
                Date(e.ModifiedUtc),
                a?.LastUsedUtc is { } lu ? Date(lu) : "",
                a is { UseCount: > 0 } ? a.UseCount.ToString(CultureInfo.InvariantCulture) : "",
                a?.TypeLabel ?? "",
                kb.GetCategory(a?.Category)?.Name ?? "",
                a?.OwnerAppName ?? rule?.Owner ?? "",
                rule?.Name ?? "",
                a is null ? "" : ReportData.ProtectionName(a.Protection),
                a?.Verdict is { } v ? ReportData.VerdictName(v, a.Facts) : "",
                a?.Verdict is null ? "" : a.Confidence.ToString(CultureInfo.InvariantCulture),
                a is null ? "" : ActionName(a.Action),
                a?.Headline ?? "",
                e.IsDirectory && a is not null ? a.ReclaimBytes.ToString(CultureInfo.InvariantCulture) : "",
                e.IsDirectory && a is not null ? a.ReviewBytes.ToString(CultureInfo.InvariantCulture) : "",
            ];
            writer.WriteLine(string.Join(",", fields.Select(Csv)));
            if (++rows % 10_000 == 0)
            {
                ct.ThrowIfCancellationRequested();
                rowsWritten?.Report(rows);
            }
        }
        rowsWritten?.Report(rows);
        return rows;
    }

    private static string Date(DateTime utc) => utc == default ? "" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 quoting, plus a leading apostrophe for cells Excel would run as formulas.</summary>
    public static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string ActionName(CleanupAction a) => a switch
    {
        CleanupAction.Delete => "Delete",
        CleanupAction.Uninstall => "Uninstall the program",
        CleanupAction.UseWindowsTool => "Use a Windows tool",
        CleanupAction.KeepOneCopy => "Keep one copy",
        CleanupAction.ClearInApp => "Remove it in its app",
        _ => "",
    };

    // ------------------------------------------------------------------ HTML

    public static void WriteHtml(ReportData r, string path) => File.WriteAllText(path, Html(r), new UTF8Encoding(false));

    public static string Html(ReportData r)
    {
        var sb = new StringBuilder(256 * 1024);
        string title = $"PuterScanny report: {r.Root}";
        sb.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(title)}}</title>
            <style>
            :root { --bg:#f7f7f9; --card:#fff; --text:#1d1d1f; --muted:#666; --line:#e3e3e8; --accent:#2b6cb0;
                    --required:#6b7280; --keep:#94a3b8; --personal:#7c6fd9; --review:#c78a00; --safe:#2e9e4a; --recommend:#1b7f3a; --tool:#2b7cd3; }
            @media (prefers-color-scheme: dark) { :root { --bg:#16161a; --card:#202026; --text:#ececf1; --muted:#a0a0aa; --line:#33333b; --accent:#7fb2f0; } }
            * { box-sizing:border-box; }
            body { margin:0; font:15px/1.5 "Segoe UI", system-ui, sans-serif; background:var(--bg); color:var(--text); }
            main { max-width:1200px; margin:0 auto; padding:24px 16px 64px; }
            h1 { font-size:26px; margin:0 0 4px; } h2 { font-size:19px; margin:32px 0 8px; } .muted { color:var(--muted); }
            .cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(170px,1fr)); gap:12px; margin:20px 0; }
            .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px 16px; }
            .card .n { font-size:24px; font-weight:600; } .card .l { color:var(--muted); font-size:13px; }
            .bar { display:flex; height:22px; border-radius:6px; overflow:hidden; border:1px solid var(--line); }
            .bar span { display:block; height:100%; } .legend { display:flex; flex-wrap:wrap; gap:14px; margin-top:8px; font-size:13px; }
            .legend i { display:inline-block; width:11px; height:11px; border-radius:3px; margin-right:5px; vertical-align:-1px; }
            .scroll { overflow-x:auto; background:var(--card); border:1px solid var(--line); border-radius:10px; }
            table { border-collapse:collapse; width:100%; font-size:13.5px; }
            th, td { text-align:left; padding:7px 10px; border-bottom:1px solid var(--line); vertical-align:top; }
            th { position:sticky; top:0; background:var(--card); font-weight:600; } td.num, th.num { text-align:right; white-space:nowrap; }
            td.path { word-break:break-all; color:var(--muted); font-size:12.5px; }
            .pill { display:inline-block; padding:1px 8px; border-radius:20px; color:#fff; font-size:12px; white-space:nowrap; }
            details summary { cursor:pointer; color:var(--accent); margin:8px 0; }
            .hbar { height:8px; background:var(--accent); border-radius:4px; min-width:2px; }
            footer { margin-top:40px; font-size:12.5px; color:var(--muted); }
            </style>
            </head>
            <body><main>
            <h1>{{E(title)}}</h1>
            <div class="muted">Scanned {{E(r.ScannedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture))}} on {{E(r.Machine)}}
             · {{r.Files:N0}} files in {{r.Folders:N0}} folders · {{E(ByteSize.Format(r.TotalSize))}}{{(r.Elevated ? " · administrator" : "")}}
             {{(r.Complete ? "" : " · <strong>the scan did not finish, so results are partial</strong>")}}</div>
            """);

        // ---- headline numbers
        sb.Append("<div class=\"cards\">");
        Card(sb, ByteSize.Format(r.CanFree), "can be freed safely");
        Card(sb, ByteSize.Format(r.Recommended), "recommended to delete");
        Card(sb, ByteSize.Format(r.Review), "worth reviewing");
        Card(sb, ByteSize.Format(r.WithWindowsTools), "freeable with Windows tools");
        Card(sb, ByteSize.Format(r.DuplicatesWasted), "taken by duplicate copies");
        Card(sb, r.UninstallSuggestions.Count.ToString("N0", CultureInfo.CurrentCulture), "programs you might uninstall");
        sb.Append("</div>");
        sb.Append("<p class=\"muted\">PuterScanny never deletes anything by itself. Anything Windows or a program needs is always kept, and your own files are never more than \"Review\".</p>");

        // ---- where the space goes
        long verdictTotal = Math.Max(1, r.Verdicts.Sum(v => v.Bytes));
        sb.Append("<h2>Where the space goes</h2><div class=\"bar\">");
        foreach (var v in r.Verdicts)
            sb.Append($"<span title=\"{E(v.Verdict)}: {E(ByteSize.Format(v.Bytes))}\" style=\"width:{100.0 * v.Bytes / verdictTotal:0.###}%;background:{VerdictColor(v.Verdict)}\"></span>");
        sb.Append("</div><div class=\"legend\">");
        foreach (var v in r.Verdicts)
            sb.Append($"<span><i style=\"background:{VerdictColor(v.Verdict)}\"></i>{E(v.Verdict)}: {E(ByteSize.Format(v.Bytes))} ({v.Files:N0} files)</span>");
        sb.Append("</div>");

        // ---- clean-up lists
        ItemTable(sb, "Delete: safe and recommended", "The biggest things that can go. A folder listed here can go as a whole.", r.ToDelete);
        ItemTable(sb, "Free with Windows tools", "Use the named Windows tool rather than deleting these by hand.", r.WindowsTools);
        ItemTable(sb, "Review: your call", "These could go, but look first.", r.ToReview);

        if (r.UninstallSuggestions.Count > 0)
        {
            sb.Append("<h2>Programs you might uninstall</h2><p class=\"muted\">Uninstall from Settings &gt; Apps &gt; Installed apps, never by deleting their folders.</p><div class=\"scroll\"><table><tr><th>Program</th><th class=\"num\">Size</th><th>Last used</th><th>Why</th></tr>");
            foreach (var p in r.UninstallSuggestions)
                sb.Append($"<tr><td>{E(p.Name)}<div class=\"muted\">{E(p.Publisher ?? "")} {E(p.Version ?? "")}</div></td><td class=\"num\">{E(ByteSize.Format(p.SizeHere))}</td><td>{E(p.LastUsed ?? "No record")}</td><td>{E(p.Suggestion ?? "")}</td></tr>");
            sb.Append("</table></div>");
        }

        if (r.Duplicates.Count > 0)
        {
            sb.Append("<h2>Duplicate files</h2><p class=\"muted\">Identical byte for byte. Keep the first copy listed; the others can go.</p>");
            Collapsible(sb, r.Duplicates.Count, 30, start =>
            {
                sb.Append("<div class=\"scroll\"><table><tr><th>Copy to keep</th><th class=\"num\">Copies</th><th class=\"num\">Wasted</th><th>Other copies</th></tr>");
                foreach (var d in r.Duplicates.Skip(start.Skip).Take(start.Take))
                    sb.Append($"<tr><td class=\"path\">{E(d.Keep)}</td><td class=\"num\">{d.Copies}</td><td class=\"num\">{E(ByteSize.Format(d.Wasted))}</td><td class=\"path\">{string.Join("<br>", d.Others.Select(E))}</td></tr>");
                sb.Append("</table></div>");
            });
        }

        // ---- overview of contents
        sb.Append("<h2>What's in it</h2><div class=\"scroll\"><table><tr><th>Kind of file</th><th class=\"num\">Files</th><th class=\"num\">Size</th><th style=\"width:35%\"></th></tr>");
        long catMax = Math.Max(1, r.Categories.Count == 0 ? 1 : r.Categories.Max(c => c.Bytes));
        foreach (var c in r.Categories)
            sb.Append($"<tr><td>{E(c.Name)}</td><td class=\"num\">{c.Files:N0}</td><td class=\"num\">{E(ByteSize.Format(c.Bytes))}</td><td><div class=\"hbar\" style=\"width:{100.0 * c.Bytes / catMax:0.#}%\"></div></td></tr>");
        sb.Append("</table></div>");

        ItemTable(sb, "Biggest folders", "The largest folders directly inside the scanned location.", r.TopFolders, showVerdict: true);
        ItemTable(sb, "Largest files", "", r.LargestFiles, showVerdict: true);
        ItemTable(sb, "Not identified", "The biggest items (over 1 MB) that no installed program claims, that aren't in a known location, and whose type isn't known. " +
            "Where the optional local AI was used, its guess is shown; it can be wrong and never changes a recommendation.", r.Unidentified, showVerdict: true);

        sb.Append("<h2>What must be kept</h2><div class=\"scroll\"><table><tr><th>Level</th><th class=\"num\">Files</th><th class=\"num\">Size</th></tr>");
        foreach (var p in r.ProtectionLevels)
            sb.Append($"<tr><td>{E(p.Level)}</td><td class=\"num\">{p.Files:N0}</td><td class=\"num\">{E(ByteSize.Format(p.Bytes))}</td></tr>");
        sb.Append("</table></div>");

        // ---- coverage
        sb.Append("<h2>What was checked</h2><div class=\"scroll\"><table><tr><th>Check</th><th>Status</th><th>Details</th></tr>");
        foreach (var n in r.Coverage)
            sb.Append($"<tr><td>{E(n.Topic)}</td><td>{E(n.Status)}</td><td>{E(n.Detail)}</td></tr>");
        sb.Append("</table></div>");

        if (r.Skipped.Count > 0)
        {
            sb.Append($"<h2>Skipped or not fully scanned ({r.SkippedItems:N0})</h2><p class=\"muted\">Places the scan could not look inside, so nothing is silently missed. Links are not followed on purpose.</p>");
            Collapsible(sb, r.Skipped.Count, 20, part =>
            {
                sb.Append("<div class=\"scroll\"><table><tr><th>What happened</th><th>Location</th><th>Details</th></tr>");
                foreach (var n in r.Skipped.Skip(part.Skip).Take(part.Take))
                    sb.Append($"<tr><td>{E(n.Topic)}</td><td class=\"path\">{E(n.Status)}</td><td>{E(n.Detail)}</td></tr>");
                sb.Append("</table></div>");
            });
        }

        sb.Append($"<footer>Made by PuterScanny {E(r.AppVersion)} — free, offline, and nothing leaves your PC. Sizes use 1 KB = 1024 bytes, like File Explorer.</footer></main></body></html>");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string number, string label) =>
        sb.Append($"<div class=\"card\"><div class=\"n\">{E(number)}</div><div class=\"l\">{E(label)}</div></div>");

    private static void ItemTable(StringBuilder sb, string heading, string intro, IReadOnlyList<ReportItem> items, bool showVerdict = true)
    {
        if (items.Count == 0) return;
        sb.Append($"<h2>{E(heading)} ({items.Count:N0})</h2>");
        if (intro.Length > 0) sb.Append($"<p class=\"muted\">{E(intro)}</p>");
        Collapsible(sb, items.Count, 25, part =>
        {
            sb.Append("<div class=\"scroll\"><table><tr><th>Item</th><th class=\"num\">Size</th>");
            if (showVerdict) sb.Append("<th>Recommendation</th>");
            sb.Append("<th>Why</th><th>What it is</th></tr>");
            foreach (var i in items.Skip(part.Skip).Take(part.Take))
            {
                sb.Append($"<tr><td>{E(i.Name)}<div class=\"path\">{E(i.Path)}</div></td><td class=\"num\">{E(ByteSize.Format(i.Size))}</td>");
                if (showVerdict) sb.Append($"<td><span class=\"pill\" style=\"background:{VerdictColor(i.Verdict)}\">{E(i.Verdict)}</span> <span class=\"muted\">{i.Confidence}%</span></td>");
                sb.Append($"<td>{E(i.Reason)}</td><td>{E(i.WhatItIs)}{(i.BelongsTo.Length > 0 ? $"<div class=\"muted\">{E(i.BelongsTo)}</div>" : "")}");
                if (i.AiNote is { } ai) sb.Append($"<div class=\"muted\" style=\"font-style:italic;margin-top:4px\">AI-written guess: {E(ai)}</div>");
                sb.Append("</td></tr>");
            }
            sb.Append("</table></div>");
        });
    }

    /// <summary>Shows the first rows, and the rest inside a "Show N more" expander.</summary>
    private static void Collapsible(StringBuilder sb, int count, int first, Action<(int Skip, int Take)> render)
    {
        render((0, first));
        if (count <= first) return;
        sb.Append($"<details><summary>Show {count - first:N0} more</summary>");
        render((first, count - first));
        sb.Append("</details>");
    }

    public static string VerdictColor(string verdict) => verdict switch
    {
        "Required by Windows" => "var(--required)",
        "Keep" => "var(--keep)",
        "No reason to remove" => "#c4cbd6",
        "Keep (your file)" => "var(--personal)",
        "Review" => "var(--review)",
        "Safe to delete" => "var(--safe)",
        "Recommended to delete" => "var(--recommend)",
        _ => "var(--tool)",
    };

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
