namespace PuterScanny.Core.Models;

/// <summary>File dates that don't mean what they seem to.</summary>
public static class FileDates
{
    /// <summary>
    /// TorrentZIP, used for ROM sets and emulator files, stamps every file it packs with the same date: 24 Dec 1996,
    /// 23:32 (MAME's first release). Zip dates carry no time zone, so once unpacked the file shows that local time on
    /// any PC. It says nothing about when the file was made or changed.
    /// </summary>
    public static bool IsTorrentZipDate(DateTime utc)
    {
        if (utc == default) return false;
        var local = utc.ToLocalTime();
        if (local is { Year: 1996, Month: 12, Day: 24, Hour: 23, Minute: 32 }) return true;
        // Also when it was kept as the original GMT+1 moment (22:32 UTC).
        return utc is { Year: 1996, Month: 12, Day: 24, Hour: 22, Minute: 32 };
    }

    public static readonly DateTime TorrentZipLocal = new(1996, 12, 24, 23, 32, 0, DateTimeKind.Unspecified);
}
