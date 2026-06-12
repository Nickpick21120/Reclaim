using System.IO;
using System.Windows.Media;
using Reclaim.App.Services;
using Reclaim.Core.Cleanup;
using Reclaim.Core.Formatting;
using Reclaim.Core.Scanning;

namespace Reclaim.App.ViewModels;

/// <summary>One row in the flat list: a file or folder with its size and
/// location. When a cleanup finding is supplied, the row is flagged as
/// reclaimable and carries that finding's safety tier and explanation.</summary>
public sealed class FlatItemViewModel(FileSystemNode node, CleanupFinding? reclaimable = null)
{
    public FileSystemNode Node { get; } = node;

    public string Name => Node.Name;
    public bool IsDirectory => Node.IsDirectory;
    public string SizeText => ByteSize.Format(Node.SizeBytes);
    public long SizeBytes => Node.SizeBytes;
    public ImageSource? Icon => ShellIconProvider.GetIcon(Node.FullPath, Node.IsDirectory);

    // ---- Cleanup flagging ----
    public bool IsReclaimable => reclaimable is not null;
    public string ReclaimableLabel =>
        reclaimable is null ? "" : SafetyDisplay.Label(reclaimable.Safety);
    public Brush ReclaimableBrush
    {
        get
        {
            if (reclaimable is null) return Brushes.Transparent;
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(
                SafetyDisplay.Color(reclaimable.Safety))!;
            b.Freeze();
            return b;
        }
    }
    public string ReclaimableTooltip => reclaimable?.Rule.Explanation ?? "";

    /// <summary>Whether this individual file can be cleaned via right-click:
    /// it must be reclaimable and in a safe (in-app deletable) tier.</summary>
    public bool CanCleanThis =>
        reclaimable is not null
        && !Node.IsDirectory
        && DeletionEngine.IsDeletableTier(reclaimable.Safety);

    /// <summary>The cleanup finding this row belongs to, if any.</summary>
    public CleanupFinding? Finding => reclaimable;

    /// <summary>The containing folder, so the Name column stays readable and the
    /// full path lives in its own column.</summary>
    public string Location => Path.GetDirectoryName(Node.FullPath) ?? Node.FullPath;

    public string FullPath => Node.FullPath;
}
