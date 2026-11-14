using System.Globalization;
using PuterScanny.Analysis.Reporting;
using PuterScanny.Core.Formatting;
using PuterScanny.Core.Models;

namespace PuterScanny.Chat;

/// <summary>Plain-English formatting for chat answers (the same wording as the rest of the app).</summary>
internal static class ChatText
{
    public static string Size(long bytes) => ByteSize.Format(bytes);

    public static string Count(long n, string singular, string plural) =>
        $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? singular : plural)}";

    public static string Verdict(Verdict v, FactFlags facts) => ReportData.VerdictName(v, facts);

    /// <summary>"today", "3 days ago", "5 months ago"…</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var days = (nowUtc - utc).TotalDays;
        if (days < 1) return "today";
        if (days < 2) return "yesterday";
        if (days < 14) return $"{(int)days} days ago";
        if (days < 60) return $"{(int)(days / 7)} weeks ago";
        if (days < 730) return $"{(int)(days / 30.4)} months ago";
        return $"{(days / 365.25).ToString("0.#", CultureInfo.CurrentCulture)} years ago";
    }

    /// <summary>"365 days" → "a year", "730" → "2 years", "90" → "3 months".</summary>
    public static string Period(int days)
    {
        if (days % 365 == 0) return days == 365 ? "a year" : $"{days / 365} years";
        if (days % 30 == 0 || days % 31 == 0) { int m = (int)Math.Round(days / 30.4); return m == 1 ? "a month" : $"{m} months"; }
        if (days % 7 == 0) return days == 7 ? "a week" : $"{days / 7} weeks";
        return days == 1 ? "a day" : $"{days} days";
    }

    /// <summary>"a, b and c".</summary>
    public static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    /// <summary>The path relative to the folder being asked about ("Games\retip"), or the full path when it's elsewhere.</summary>
    public static string Relative(string path, string scope)
    {
        string prefix = scope.EndsWith('\\') ? scope : scope + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length ? path[prefix.Length..] : path;
    }

    /// <summary>The last part of a path ("Downloads"), or the drive ("D:\").</summary>
    public static string FolderName(string path)
    {
        string trimmed = path.Length > 3 ? path.TrimEnd('\\') : path;
        string name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public static string Sentence(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
}
