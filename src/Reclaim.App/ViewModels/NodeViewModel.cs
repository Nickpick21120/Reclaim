using System.Collections.ObjectModel;
using System.Windows.Media;
using Reclaim.App.Services;
using Reclaim.Core.Formatting;
using Reclaim.Core.Scanning;

namespace Reclaim.App.ViewModels;

/// <summary>
/// Wraps a <see cref="FileSystemNode"/> for the tree view. Children are only
/// materialized when first accessed (i.e. when the user expands the node),
/// so a million-node scan doesn't create a million view models up front.
/// </summary>
public sealed class NodeViewModel : ViewModelBase
{
    private const double MaxBarWidth = 56;

    private ObservableCollection<NodeViewModel>? _children;
    private bool _isExpanded;
    private bool _isSelected;

    public NodeViewModel(FileSystemNode node)
    {
        Node = node;
    }

    public FileSystemNode Node { get; }

    public string Name => Node.Name;
    public bool IsDirectory => Node.IsDirectory;
    public bool HadError => Node.HadError;
    public string SizeText => ByteSize.Format(Node.SizeBytes);
    public string PercentText => Node.Parent is null ? "" : Node.FractionOfParent.ToString("P0");
    public double BarWidth => Math.Max(1, Node.FractionOfParent * MaxBarWidth);

    /// <summary>Bar color scaled by share-of-parent: a calm blue for small
    /// shares warming through to a hot amber/red for the dominant items, so the
    /// eye is drawn to what's actually filling each folder.</summary>
    public Brush BarBrush
    {
        get
        {
            var t = Math.Clamp(Node.FractionOfParent, 0, 1);
            var blue = Color.FromRgb(0x2D, 0x6B, 0xFF);
            var teal = Color.FromRgb(0x3F, 0xB4, 0x77);
            var red = Color.FromRgb(0xE8, 0x55, 0x6B);
            // Blue → teal for the first half of the range, teal → red for the second.
            var c = t < 0.5 ? Lerp(blue, teal, t / 0.5) : Lerp(teal, red, (t - 0.5) / 0.5);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Native Windows shell icon for this file/folder.</summary>
    public ImageSource? Icon => ShellIconProvider.GetIcon(Node.FullPath, Node.IsDirectory);

    public string Tooltip =>
        $"{Node.FullPath}\n{SizeText} — {Node.FileCount:N0} files" +
        (HadError ? "\n⚠ Some contents could not be read" : "");

    public ObservableCollection<NodeViewModel> Children =>
        _children ??= new ObservableCollection<NodeViewModel>(
            Node.Children.Select(c => new NodeViewModel(c)));

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>After an in-memory prune, re-notify size/percent bindings and
    /// drop any child view models whose underlying node was removed. Only walks
    /// already-materialized children, so it stays cheap on large trees.</summary>
    public void RefreshSizes()
    {
        Raise(nameof(SizeText));
        Raise(nameof(PercentText));
        Raise(nameof(BarWidth));
        Raise(nameof(BarBrush));
        Raise(nameof(Tooltip));

        if (_children is null)
            return;

        // Remove view models for nodes no longer in the tree.
        for (var i = _children.Count - 1; i >= 0; i--)
        {
            if (_children[i].Node.Parent is null)
                _children.RemoveAt(i);
        }
        foreach (var child in _children)
            child.RefreshSizes();
    }
}
