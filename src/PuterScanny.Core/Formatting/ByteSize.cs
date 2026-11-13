using System.Globalization;

namespace PuterScanny.Core.Formatting;

public static class ByteSize
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count the way File Explorer does (1 KB = 1024 bytes).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "N0" : value >= 10 ? "N1" : "N2";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {Units[unit]}";
    }
}
