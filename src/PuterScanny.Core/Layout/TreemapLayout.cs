namespace PuterScanny.Core.Layout;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk): splits a rectangle into one box per value, each with an
/// area proportional to its value, keeping boxes as close to square as possible so they're easy to read and click.
/// </summary>
public static class TreemapLayout
{
    /// <summary>Lays out <paramref name="values"/> (any order; zero or negative values get an empty box).</summary>
    public static LayoutRect[] Squarify(IReadOnlyList<double> values, LayoutRect bounds)
    {
        var result = new LayoutRect[values.Count];
        double total = 0;
        foreach (double v in values) if (v > 0) total += v;
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        // Biggest first gives the best aspect ratios; remember original positions.
        var order = Enumerable.Range(0, values.Count).Where(i => values[i] > 0).OrderByDescending(i => values[i]).ToList();
        double scale = bounds.Area / total;
        var areas = order.Select(i => values[i] * scale).ToList();

        var free = bounds;
        int start = 0;
        while (start < order.Count)
        {
            double side = Math.Min(free.Width, free.Height);
            int end = start + 1;
            double rowSum = areas[start];
            double worst = Worst(areas, start, end, rowSum, side);
            while (end < order.Count)
            {
                double nextSum = rowSum + areas[end];
                double nextWorst = Worst(areas, start, end + 1, nextSum, side);
                if (nextWorst > worst) break;
                rowSum = nextSum;
                worst = nextWorst;
                end++;
            }
            free = PlaceRow(areas, order, start, end, rowSum, free, result);
            start = end;
        }
        return result;
    }

    /// <summary>The worst (largest) aspect ratio in a row laid along a side of length <paramref name="side"/>.</summary>
    private static double Worst(List<double> areas, int start, int end, double sum, double side)
    {
        double max = double.MinValue, min = double.MaxValue;
        for (int i = start; i < end; i++)
        {
            max = Math.Max(max, areas[i]);
            min = Math.Min(min, areas[i]);
        }
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static LayoutRect PlaceRow(List<double> areas, List<int> order, int start, int end, double sum, LayoutRect free, LayoutRect[] result)
    {
        if (free.Width >= free.Height)
        {
            // Column on the left side.
            double width = sum / free.Height;
            double y = free.Y;
            for (int i = start; i < end; i++)
            {
                double h = areas[i] / width;
                result[order[i]] = new LayoutRect(free.X, y, width, h);
                y += h;
            }
            return new LayoutRect(free.X + width, free.Y, Math.Max(0, free.Width - width), free.Height);
        }
        else
        {
            // Row along the top.
            double height = sum / free.Width;
            double x = free.X;
            for (int i = start; i < end; i++)
            {
                double w = areas[i] / height;
                result[order[i]] = new LayoutRect(x, free.Y, w, height);
                x += w;
            }
            return new LayoutRect(free.X, free.Y + height, free.Width, Math.Max(0, free.Height - height));
        }
    }
}
