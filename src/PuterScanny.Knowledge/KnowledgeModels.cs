namespace PuterScanny.Knowledge;

public sealed record FileCategory(string Id, string Name, string Description);

public sealed record FileTypeInfo(string Extension, string Description, string Category);

/// <summary>A byte pattern that identifies a file format by its content.</summary>
public sealed record MagicSignature(
    string Id,
    string Name,
    string Category,
    int Offset,
    byte?[] Pattern,
    IReadOnlyList<string> Extensions,
    bool IsContainer,
    bool IsWeak)
{
    public bool Matches(ReadOnlySpan<byte> head)
    {
        if (Offset + Pattern.Length > head.Length) return false;
        for (int i = 0; i < Pattern.Length; i++)
        {
            if (Pattern[i] is byte b && head[Offset + i] != b) return false;
        }
        return true;
    }

    /// <summary>How many bytes of the file this signature needs to see.</summary>
    public int RequiredLength => Offset + Pattern.Length;
}

public sealed record LocationCategory(string Id, string Name);

/// <summary>A known folder or file (for example "Chrome cache" or "Windows Update downloads").</summary>
public sealed record LocationRule(
    string Id,
    string Name,
    string Description,
    string Category,
    string? Owner,
    string? Resolver,
    IReadOnlyList<string> Paths,
    string? Protect = null,
    LocationAdvice? Advice = null)
{
    /// <summary>The protection this location gives everything inside it (unless a more specific rule says otherwise).</summary>
    public PuterScanny.Core.Models.Protection Protection => Protect switch
    {
        "system" => PuterScanny.Core.Models.Protection.Required,
        "program" => PuterScanny.Core.Models.Protection.Caution,   // an installed program's own files get UninstallOnly via ownership
        "caution" => PuterScanny.Core.Models.Protection.Caution,
        _ => PuterScanny.Core.Models.Protection.None,
    };
}

/// <summary>What to do with a known location's contents.</summary>
/// <param name="Verdict">recommend, safe, review, keep, tool or inapp.</param>
/// <param name="MinAgeDays">Only items not changed or used for this many days qualify.</param>
/// <param name="DeleteFolder">The matched folder itself can go too (not just what's inside).</param>
/// <param name="Tool">For "tool": the Windows tool to use instead of deleting by hand.</param>
public sealed record LocationAdvice(string Verdict, string Why, int MinAgeDays, bool DeleteFolder, string? Tool);
