namespace Reclaim.Core.Treemap;

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double px, double py) =>
        px >= X && px < Right && py >= Y && py < Bottom;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk 2000).
/// Pure math, no UI dependency, so it can be unit tested anywhere.
/// </summary>
public static class Squarifier
{
    /// <summary>
    /// Lays out <paramref name="sizes"/> (which MUST be sorted descending and
    /// strictly positive) inside <paramref name="bounds"/>. Returns one rect per
    /// size, in the same order. Rect areas are proportional to the sizes and
    /// together exactly tile the bounds.
    /// </summary>
    public static RectD[] Layout(IReadOnlyList<long> sizes, RectD bounds)
    {
        var result = new RectD[sizes.Count];
        if (sizes.Count == 0 || bounds.Area <= 0)
            return result;

        long total = 0;
        foreach (var s in sizes) total += s;
        if (total <= 0)
            return result;

        var scale = bounds.Area / total;
        var remaining = bounds;

        var rowStart = 0;
        double rowArea = 0;

        for (var i = 0; i < sizes.Count; i++)
        {
            var area = sizes[i] * scale;
            var side = Math.Min(remaining.Width, remaining.Height);
            var count = i - rowStart;

            if (count > 0 &&
                WorstAspect(sizes, rowStart, i, scale, rowArea + area, side) >
                WorstAspect(sizes, rowStart, i - 1, scale, rowArea, side))
            {
                // Adding this item makes the row worse — flush the current row first.
                LayRow(sizes, rowStart, i - 1, scale, rowArea, ref remaining, result);
                rowStart = i;
                rowArea = 0;
            }

            rowArea += area;
        }

        LayRow(sizes, rowStart, sizes.Count - 1, scale, rowArea, ref remaining, result);
        return result;
    }

    /// <summary>Worst (largest) aspect ratio among items [from..to] if laid as one
    /// row along a side of the given length.</summary>
    private static double WorstAspect(
        IReadOnlyList<long> sizes, int from, int to, double scale, double rowArea, double side)
    {
        if (rowArea <= 0 || side <= 0)
            return double.MaxValue;

        // Sizes are sorted descending, so the extremes are the endpoints.
        var maxArea = sizes[from] * scale;
        var minArea = sizes[to] * scale;

        var s2 = rowArea * rowArea;
        var w2 = side * side;
        return Math.Max(w2 * maxArea / s2, s2 / (w2 * minArea));
    }

    private static void LayRow(
        IReadOnlyList<long> sizes, int from, int to, double scale, double rowArea,
        ref RectD remaining, RectD[] result)
    {
        if (rowArea <= 0)
        {
            for (var i = from; i <= to; i++)
                result[i] = new RectD(remaining.X, remaining.Y, 0, 0);
            return;
        }

        // Lay the row along the shorter side of the remaining area.
        if (remaining.Width >= remaining.Height)
        {
            var rowWidth = remaining.Height > 0 ? rowArea / remaining.Height : 0;
            var y = remaining.Y;
            for (var i = from; i <= to; i++)
            {
                var h = sizes[i] * scale / rowWidth;
                result[i] = new RectD(remaining.X, y, rowWidth, h);
                y += h;
            }
            remaining = new RectD(
                remaining.X + rowWidth, remaining.Y,
                Math.Max(0, remaining.Width - rowWidth), remaining.Height);
        }
        else
        {
            var rowHeight = remaining.Width > 0 ? rowArea / remaining.Width : 0;
            var x = remaining.X;
            for (var i = from; i <= to; i++)
            {
                var w = sizes[i] * scale / rowHeight;
                result[i] = new RectD(x, remaining.Y, w, rowHeight);
                x += w;
            }
            remaining = new RectD(
                remaining.X, remaining.Y + rowHeight,
                remaining.Width, Math.Max(0, remaining.Height - rowHeight));
        }
    }
}
