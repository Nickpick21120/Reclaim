using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Reclaim.Core.Formatting;
using Reclaim.Core.Scanning;
using Reclaim.Core.Treemap;

namespace Reclaim.App.Controls;

/// <summary>Treemap region-labeling strategies, for comparing legibility.</summary>
public enum TreemapLabelMode
{
    /// <summary>No labels — pure blocks (original, most decluttered).</summary>
    None,
    /// <summary>Draw the folder/file name centered on any block big enough.</summary>
    BigBlocks,
    /// <summary>Draw a header strip with the folder name across the top of each
    /// directory region (WinDirStat-style).</summary>
    FolderHeaders,
}

/// <summary>
/// Renders a squarified treemap of the subtree under <see cref="RootNode"/>.
/// Layout math lives in Reclaim.Core.Treemap.Squarifier (unit tested); this
/// control only recurses, colors, and draws. Every leaf file gets a rectangle
/// proportional to its size; colors derive from file extensions so piles of
/// the same kind of data read as patches of the same hue.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double MinVisible = 2.5;  // skip rects smaller than this (px)
    private const double MinRecurse = 6.0;  // don't subdivide rects smaller than this

    private sealed class LayoutNode
    {
        public required FileSystemNode Node { get; init; }
        public required RectD Rect { get; init; }
        public List<LayoutNode> Children { get; } = [];
        /// <summary>For FolderHeaders mode: the strip at the top of this dir's rect.</summary>
        public RectD? HeaderRect { get; set; }
    }

    private const double HeaderHeight = 15.0;

    private LayoutNode? _layout;
    private readonly Dictionary<string, SolidColorBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Pen BorderPen = MakeFrozenPen(Color.FromArgb(160, 0, 0, 0), 1.0);
    private static readonly Brush EmptyBrush = MakeFrozenBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
    private static readonly Brush HintBrush = MakeFrozenBrush(Color.FromRgb(0x7A, 0x83, 0x98));
    private static readonly SolidColorBrush DirBrush = MakeFrozenBrush(Color.FromRgb(0x1A, 0x22, 0x36));
    private static readonly Brush HeaderBrush = MakeFrozenBrush(Color.FromArgb(220, 0x10, 0x16, 0x24));
    private static readonly Brush LabelBrush = MakeFrozenBrush(Color.FromArgb(235, 0xE6, 0xEC, 0xF7));
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(
        nameof(RootNode), typeof(FileSystemNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            static (d, _) => ((TreemapControl)d)._layout = null));

    public FileSystemNode? RootNode
    {
        get => (FileSystemNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    /// <summary>Files smaller than this are omitted from the map, so the view
    /// shows only blocks worth caring about. Directories are always recursed
    /// (their visible size reflects only the files that pass the filter).</summary>
    public static readonly DependencyProperty MinFileBytesProperty = DependencyProperty.Register(
        nameof(MinFileBytes), typeof(long), typeof(TreemapControl),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender,
            static (d, _) => ((TreemapControl)d)._layout = null));

    public long MinFileBytes
    {
        get => (long)GetValue(MinFileBytesProperty);
        set => SetValue(MinFileBytesProperty, value);
    }

    /// <summary>How to label regions on the map, for comparing legibility approaches.</summary>
    public static readonly DependencyProperty LabelModeProperty = DependencyProperty.Register(
        nameof(LabelMode), typeof(TreemapLabelMode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(TreemapLabelMode.None,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public TreemapLabelMode LabelMode
    {
        get => (TreemapLabelMode)GetValue(LabelModeProperty);
        set => SetValue(LabelModeProperty, value);
    }

    /// <summary>Raised on double-click; argument is the immediate child directory
    /// of <see cref="RootNode"/> under the cursor.</summary>
    public event Action<FileSystemNode>? DrillRequested;

    public TreemapControl()
    {
        ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 250);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _layout = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(EmptyBrush, null, bounds);

        if (RootNode is not { SizeBytes: > 0 } root || bounds.Width < 4 || bounds.Height < 4)
        {
            var hint = new FormattedText(
                "Scan a folder to see its treemap",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, HintBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hint, new Point(
                (bounds.Width - hint.Width) / 2, (bounds.Height - hint.Height) / 2));
            return;
        }

        _layout ??= BuildLayout(root, new RectD(0, 0, bounds.Width, bounds.Height), MinFileBytes, LabelMode);
        RenderNode(dc, _layout);
    }

    // ---- layout -------------------------------------------------------------

    /// <summary>Returns the visible size of a node under the current filter:
    /// for a file, its size if it passes the threshold else 0; for a directory,
    /// the sum of its children's visible sizes.</summary>
    private static long VisibleSize(FileSystemNode node, long minFileBytes)
    {
        if (!node.IsDirectory)
            return node.SizeBytes >= minFileBytes ? node.SizeBytes : 0;

        // No filter: the precomputed total is exact and free.
        if (minFileBytes <= 0)
            return node.SizeBytes;

        long sum = 0;
        foreach (var child in node.Children)
            sum += VisibleSize(child, minFileBytes);
        return sum;
    }

    private static LayoutNode BuildLayout(
        FileSystemNode node, RectD rect, long minFileBytes, TreemapLabelMode labelMode)
    {
        var layout = new LayoutNode { Node = node, Rect = rect };

        if (!node.IsDirectory || rect.Width < MinRecurse || rect.Height < MinRecurse)
            return layout;

        // In FolderHeaders mode, reserve a strip at the top for this folder's
        // name and lay children out below it — only when there's room.
        var childRect = rect;
        if (labelMode == TreemapLabelMode.FolderHeaders &&
            node.Parent is not null &&
            rect.Height > HeaderHeight * 2.5 &&
            rect.Width > 40)
        {
            layout.HeaderRect = new RectD(rect.X, rect.Y, rect.Width, HeaderHeight);
            childRect = new RectD(rect.X, rect.Y + HeaderHeight, rect.Width, rect.Height - HeaderHeight);
        }

        // Children with any visible content, paired with that visible size,
        // sorted descending (the squarifier requires descending order).
        var visible = new List<(FileSystemNode Node, long Size)>();
        foreach (var child in node.Children)
        {
            var size = VisibleSize(child, minFileBytes);
            if (size > 0)
                visible.Add((child, size));
        }
        if (visible.Count == 0)
        {
            layout.HeaderRect = null; // don't strand a header over nothing
            return layout;
        }
        visible.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        var rects = Squarifier.Layout(visible.Select(v => v.Size).ToList(), childRect);

        for (var i = 0; i < visible.Count; i++)
        {
            if (rects[i].Width < MinVisible || rects[i].Height < MinVisible)
                continue;
            layout.Children.Add(BuildLayout(visible[i].Node, rects[i], minFileBytes, labelMode));
        }

        return layout;
    }

    // ---- rendering ------------------------------------------------------------

    private void RenderNode(DrawingContext dc, LayoutNode layout)
    {
        if (layout.Children.Count == 0)
        {
            // Leaf: a file, or a directory too small to subdivide at this zoom.
            var r = layout.Rect;
            dc.DrawRectangle(BrushFor(layout.Node), BorderPen,
                new Rect(r.X, r.Y, r.Width, r.Height));

            if (LabelMode == TreemapLabelMode.BigBlocks)
                DrawCenteredLabel(dc, layout.Node.Name, r);
            return;
        }

        // Directory header strip (FolderHeaders mode).
        if (layout.HeaderRect is { } hr)
        {
            dc.DrawRectangle(HeaderBrush, null, new Rect(hr.X, hr.Y, hr.Width, hr.Height));
            DrawHeaderLabel(dc, layout.Node.Name, hr);
        }

        foreach (var child in layout.Children)
            RenderNode(dc, child);
    }

    private void DrawCenteredLabel(DrawingContext dc, string text, RectD rect)
    {
        // Only label blocks comfortably large enough to read.
        if (rect.Width < 46 || rect.Height < 18)
            return;

        var ft = MakeText(text, 11, LabelBrush, rect.Width - 6);
        if (ft.Width > rect.Width - 6 || ft.Height > rect.Height - 4)
            return;

        dc.DrawText(ft, new Point(
            rect.X + (rect.Width - ft.Width) / 2,
            rect.Y + (rect.Height - ft.Height) / 2));
    }

    private void DrawHeaderLabel(DrawingContext dc, string text, RectD header)
    {
        if (header.Width < 30)
            return;
        var ft = MakeText(text, 10.5, LabelBrush, header.Width - 8);
        dc.DrawText(ft, new Point(header.X + 4, header.Y + (header.Height - ft.Height) / 2));
    }

    private FormattedText MakeText(string text, double size, Brush brush, double maxWidth)
    {
        var ft = new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, LabelTypeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(8, maxWidth),
        };
        return ft;
    }

    private SolidColorBrush BrushFor(FileSystemNode node)
    {
        if (node.IsDirectory)
            return DirBrush;

        var key = Path.GetExtension(node.Name);
        if (string.IsNullOrEmpty(key))
            key = "<none>";

        if (_brushCache.TryGetValue(key, out var cached))
            return cached;

        // Deterministic hue per extension.
        var hash = 0;
        foreach (var ch in key)
            hash = hash * 31 + char.ToLowerInvariant(ch);
        var brush = MakeFrozenBrush(HsvToRgb(Math.Abs(hash) % 360, 0.45, 0.62));

        _brushCache[key] = brush;
        return brush;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = ((int)h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static SolidColorBrush MakeFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static Pen MakeFrozenPen(Color color, double thickness)
    {
        var p = new Pen(MakeFrozenBrush(color), thickness);
        p.Freeze();
        return p;
    }

    // ---- interaction ------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        var hit = HitTest(pos.X, pos.Y);
        ToolTip = hit is null ? null : $"{hit.FullPath}\n{ByteSize.Format(hit.SizeBytes)}";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount != 2 || _layout is null)
            return;

        var pos = e.GetPosition(this);
        // Find the deepest block under the cursor, not just the top-level one,
        // so a single double-click jumps straight to where the item lives.
        var hit = DeepestAt(_layout, pos.X, pos.Y);
        if (hit is null)
            return;

        // Navigate to the containing folder: if a file was clicked, focus its
        // parent folder; if a folder was clicked, enter it.
        var target = hit.IsDirectory ? hit : hit.Parent;
        if (target is { IsDirectory: true })
            DrillRequested?.Invoke(target);
    }

    /// <summary>Walks the layout tree to the deepest node whose rect contains
    /// the point, so clicks resolve to the specific block aimed at rather than
    /// its top-level ancestor.</summary>
    private static FileSystemNode? DeepestAt(LayoutNode layout, double x, double y)
    {
        if (!layout.Rect.Contains(x, y))
            return null;

        foreach (var child in layout.Children)
        {
            var deeper = DeepestAt(child, x, y);
            if (deeper is not null)
                return deeper;
        }
        return layout.Node;
    }

    private FileSystemNode? HitTest(double x, double y)
    {
        var current = _layout;
        if (current is null || !current.Rect.Contains(x, y))
            return null;

        while (true)
        {
            var next = current.Children.FirstOrDefault(c => c.Rect.Contains(x, y));
            if (next is null)
                return current.Node;
            current = next;
        }
    }
}
