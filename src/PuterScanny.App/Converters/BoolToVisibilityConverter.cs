using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PuterScanny.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Colour for a verdict tag: green to delete, amber to review, blue for Windows tools.</summary>
public sealed class VerdictTagBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush Delete = Frozen(0x2E, 0x9E, 0x4A);
    private static readonly System.Windows.Media.Brush Review = Frozen(0xC7, 0x8A, 0x00);
    private static readonly System.Windows.Media.Brush Tool = Frozen(0x2B, 0x7C, 0xD3);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Delete" or "Can delete" => Delete,
        "Review" or "Uninstall?" => Review,
        _ => Tool,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
