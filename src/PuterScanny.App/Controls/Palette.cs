using System.Windows.Media;
using PuterScanny.Core.Models;

namespace PuterScanny.App.Controls;

/// <summary>One set of colours for verdicts and kinds of file, used by the treemap, the dashboard and the report.</summary>
public static class Palette
{
    public static readonly Brush Required = Make(0x6B, 0x72, 0x80);
    public static readonly Brush Keep = Make(0x8C, 0x9B, 0xB0);
    public static readonly Brush NoReason = Make(0xC4, 0xCB, 0xD6);
    public static readonly Brush Personal = Make(0x7C, 0x6F, 0xD9);
    public static readonly Brush Review = Make(0xC7, 0x8A, 0x00);
    public static readonly Brush Safe = Make(0x2E, 0x9E, 0x4A);
    public static readonly Brush Recommend = Make(0x1B, 0x7F, 0x3A);
    public static readonly Brush Tool = Make(0x2B, 0x7C, 0xD3);
    public static readonly Brush Other = Make(0xA8, 0xA8, 0xB0);
    public static readonly Brush Unknown = Make(0xB8, 0xBC, 0xC6);

    public static Brush ForVerdict(Verdict? v, CleanupAction action = CleanupAction.None, FactFlags facts = FactFlags.None) => (v, action) switch
    {
        (_, CleanupAction.UseWindowsTool) => Tool,
        (Verdict.Keep, _) when facts.HasFlag(FactFlags.NoEvidence) => NoReason,
        (Verdict.Required, _) => Required,
        (Verdict.Keep, _) => Keep,
        (Verdict.KeepPersonal, _) => Personal,
        (Verdict.Review, _) => Review,
        (Verdict.SafeToDelete, _) => Safe,
        (Verdict.RecommendDelete, _) => Recommend,
        _ => Unknown,
    };

    private static readonly Dictionary<string, Brush> Kinds = new(StringComparer.Ordinal)
    {
        ["video"] = Make(0xD9, 0x4F, 0x4F),
        ["image"] = Make(0xE0, 0x8A, 0x2E),
        ["audio"] = Make(0xC9, 0x5C, 0xB3),
        ["document"] = Make(0x3A, 0x86, 0xD6),
        ["archive"] = Make(0x8E, 0x6C, 0x3F),
        ["disk-image"] = Make(0x8E, 0x6C, 0x3F),
        ["installer"] = Make(0x5B, 0x8C, 0x5A),
        ["executable"] = Make(0x2E, 0x9E, 0x8F),
        ["library"] = Make(0x4D, 0xA8, 0xA0),
        ["driver"] = Make(0x4D, 0xA8, 0xA0),
        ["game"] = Make(0x93, 0x5F, 0xD8),
        ["virtual-machine"] = Make(0x5E, 0x60, 0xC9),
        ["code"] = Make(0x6E, 0x9E, 0x3A),
        ["data"] = Make(0x9E, 0x93, 0x3A),
        ["database"] = Make(0x9E, 0x93, 0x3A),
        ["cache"] = Make(0x7F, 0x8C, 0x8D),
        ["temporary"] = Make(0x7F, 0x8C, 0x8D),
        ["log"] = Make(0x7F, 0x8C, 0x8D),
        ["3d"] = Make(0xC0, 0x6E, 0x52),
        ["design"] = Make(0xC0, 0x6E, 0x52),
    };

    public static Brush ForKind(string? category) => category is not null && Kinds.TryGetValue(category, out var b) ? b : Other;

    public static Brush Darker(Brush b, double factor = 0.75)
    {
        if (b is not SolidColorBrush s) return b;
        var c = s.Color;
        return Make((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    }

    public static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
