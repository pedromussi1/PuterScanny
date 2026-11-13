using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PuterScanny.Core.Layout;

namespace PuterScanny.App.Controls;

/// <summary>A box in the treemap. Folders may carry their own children (drawn nested, one level deep).</summary>
public sealed class TreemapNode
{
    public required string Label { get; init; }
    public required double Size { get; init; }
    public required Brush Fill { get; init; }
    public string Tooltip { get; init; } = "";
    public object? Tag { get; init; }
    public bool CanOpen { get; init; }
    public IReadOnlyList<TreemapNode> Children { get; init; } = [];
}

/// <summary>Draws a two-level squarified treemap. Click a box to select it; double-click a folder to open it.</summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double HeaderHeight = 16;
    private readonly List<(Rect Box, TreemapNode Node)> _hits = [];
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private TreemapNode? _hover;

    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
        nameof(Nodes), typeof(IReadOnlyList<TreemapNode>), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(TreemapNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IReadOnlyList<TreemapNode>? Nodes
    {
        get => (IReadOnlyList<TreemapNode>?)GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public TreemapNode? Selected
    {
        get => (TreemapNode?)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    /// <summary>Raised on double-click of a folder box.</summary>
    public event EventHandler<TreemapNode>? Opened;

    public TreemapControl()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 150);
        Focusable = true;
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        var nodes = Nodes;
        if (nodes is null || nodes.Count == 0 || bounds.Width < 4 || bounds.Height < 4) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1);
        outline.Freeze();
        DrawLevel(dc, nodes, bounds, depth: 0, outline, dpi);

        if (Selected is { } sel && _hits.FirstOrDefault(h => ReferenceEquals(h.Node, sel)) is { Node: not null } hit)
        {
            var pen = new Pen(SystemColors.HighlightBrush, 3);
            dc.DrawRectangle(null, pen, Inflate(hit.Box, -1.5));
        }
    }

    private void DrawLevel(DrawingContext dc, IReadOnlyList<TreemapNode> nodes, Rect area, int depth, Pen outline, double dpi)
    {
        var rects = TreemapLayout.Squarify(nodes.Select(n => n.Size).ToList(), new LayoutRect(area.X, area.Y, area.Width, area.Height));
        for (int i = 0; i < nodes.Count; i++)
        {
            var r = rects[i];
            if (r.Width < 1 || r.Height < 1) continue;
            var box = new Rect(r.X, r.Y, r.Width, r.Height);
            var node = nodes[i];

            bool nested = depth == 0 && node.Children.Count > 0 && box.Width > 60 && box.Height > 45;
            if (nested)
            {
                dc.DrawRectangle(Palette.Darker(node.Fill, 0.55), outline, box);
                DrawLabel(dc, node.Label, new Rect(box.X + 3, box.Y + 1, box.Width - 6, HeaderHeight), Brushes.White, dpi, bold: true);
                _hits.Add((box, node)); // the header area selects the folder itself
                var inner = new Rect(box.X + 2, box.Y + HeaderHeight, Math.Max(0, box.Width - 4), Math.Max(0, box.Height - HeaderHeight - 2));
                DrawLevel(dc, node.Children, inner, depth + 1, outline, dpi);
            }
            else
            {
                dc.DrawRectangle(ReferenceEquals(node, _hover) ? Palette.Darker(node.Fill, 0.85) : node.Fill, outline, box);
                if (box.Width > 34 && box.Height > 14)
                    DrawLabel(dc, node.Label, new Rect(box.X + 3, box.Y + 1, box.Width - 6, box.Height - 2), Brushes.White, dpi, bold: false);
                _hits.Add((box, node));
            }
        }
    }

    private static void DrawLabel(DrawingContext dc, string text, Rect area, Brush brush, double dpi, bool bold)
    {
        if (area.Width < 10 || area.Height < 10) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            11, brush, dpi)
        {
            MaxTextWidth = area.Width,
            MaxTextHeight = Math.Min(area.Height, 30),
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, area.TopLeft);
    }

    private static Rect Inflate(Rect r, double by)
    {
        r.Inflate(by, by);
        return r.Width > 0 && r.Height > 0 ? r : Rect.Empty;
    }

    /// <summary>The deepest box under the point (nested boxes win over their folder's frame).</summary>
    private TreemapNode? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Box.Contains(p)) return _hits[i].Node;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = HitTest(e.GetPosition(this));
        if (ReferenceEquals(node, _hover)) return;
        _hover = node;
        _tip.Content = node?.Tooltip;
        _tip.IsOpen = node is not null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } node) Selected = node;
        Focus();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2 && HitTest(e.GetPosition(this)) is { CanOpen: true } node)
        {
            Opened?.Invoke(this, node);
            e.Handled = true;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    /// <summary>Lets screen readers and UI automation find the treemap.</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
}

/// <summary>A horizontal bar split into coloured segments (used for "where the space goes").</summary>
public sealed class ProportionBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<(double Value, Brush Fill)>), typeof(ProportionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<(double Value, Brush Fill)>? Segments
    {
        get => (IReadOnlyList<(double, Brush)>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var segments = Segments;
        if (segments is null || ActualWidth <= 0) return;
        double total = segments.Sum(s => Math.Max(0, s.Value));
        if (total <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 5, 5));
        double x = 0;
        foreach (var (value, fill) in segments)
        {
            double w = ActualWidth * Math.Max(0, value) / total;
            dc.DrawRectangle(fill, null, new Rect(x, 0, w, ActualHeight));
            x += w;
        }
        dc.Pop();
    }
}
