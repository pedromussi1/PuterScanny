using static PuterScanny.Signals.Native.Shlwapi;

namespace PuterScanny.Signals.Shell;

/// <summary>What Windows itself calls a file type, and which app opens it on this PC.</summary>
public sealed record AssociationInfo(string Extension, string? FriendlyTypeName, string? OpensWith);

public static class FileAssociations
{
    public static AssociationInfo Lookup(string extension) =>
        new(extension, Query(extension, ASSOCSTR_FRIENDLYDOCNAME), Query(extension, ASSOCSTR_FRIENDLYAPPNAME));

    private static string? Query(string extension, int what)
    {
        try
        {
            uint len = 0;
            const uint flags = ASSOCF_NOTRUNCATE | ASSOCF_INIT_IGNOREUNKNOWN | ASSOCF_REMAPRUNDLL;
            AssocQueryString(flags, what, extension, null, null, ref len);
            if (len is 0 or > 4096) return null;
            char[] buf = new char[len];
            if (AssocQueryString(flags, what, extension, null, buf, ref len) != 0) return null;
            string s = new string(buf, 0, (int)Math.Max(0, len - 1)).Trim();
            return s.Length == 0 ? null : s;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
