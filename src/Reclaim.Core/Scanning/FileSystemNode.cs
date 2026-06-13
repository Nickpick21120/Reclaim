namespace Reclaim.Core.Scanning;

/// <summary>
/// A node in the scanned filesystem tree. Directories aggregate the sizes
/// and counts of everything beneath them; files are leaves.
/// </summary>
public sealed class FileSystemNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }

    /// <summary>Last-modified time (UTC) for files, read for free during directory
    /// enumeration. Default (DateTime.MinValue) for directories or when unknown.</summary>
    public DateTime LastWriteUtc { get; internal set; }

    /// <summary>Total size in bytes. For directories, the recursive sum of all contents.</summary>
    public long SizeBytes { get; internal set; }

    /// <summary>Recursive count of files under this node (1 for a file).</summary>
    public long FileCount { get; internal set; }

    /// <summary>Recursive count of directories under this node (excluding itself).</summary>
    public long DirectoryCount { get; internal set; }

    /// <summary>True if this directory could not be fully read (access denied, etc.).</summary>
    public bool HadError { get; internal set; }

    public FileSystemNode? Parent { get; internal set; }

    /// <summary>Child nodes. Empty for files. Populated and then sorted by size descending.</summary>
    public IReadOnlyList<FileSystemNode> Children => _children;
    internal List<FileSystemNode> _children = [];

    /// <summary>This node's share of its parent's size, in [0,1]. 0 when parent is empty.</summary>
    public double FractionOfParent =>
        Parent is { SizeBytes: > 0 } p ? (double)SizeBytes / p.SizeBytes : 0;

    public override string ToString() => $"{Name} ({SizeBytes:N0} bytes)";

    /// <summary>
    /// Removes this node from its parent and subtracts its size and counts from
    /// every ancestor, keeping the in-memory tree consistent after a deletion
    /// without re-walking the disk. No-op if this node has no parent (the root).
    /// </summary>
    public void RemoveFromTree()
    {
        var parent = Parent;
        if (parent is null)
            return;

        if (!parent._children.Remove(this))
            return; // already detached

        var dirs = DirectoryCount + (IsDirectory ? 1 : 0);
        for (var a = parent; a is not null; a = a.Parent)
        {
            a.SizeBytes -= SizeBytes;
            a.FileCount -= FileCount;
            a.DirectoryCount -= dirs;
            if (a.SizeBytes < 0) a.SizeBytes = 0;
            if (a.FileCount < 0) a.FileCount = 0;
            if (a.DirectoryCount < 0) a.DirectoryCount = 0;
        }

        Parent = null;
    }

    /// <summary>
    /// Empties this directory in the in-memory tree (removes all children and
    /// zeroes the aggregated size/counts they contributed), for a "delete
    /// contents" that keeps the folder. No-op for files.
    /// </summary>
    public void ClearChildrenFromTree()
    {
        if (!IsDirectory || _children.Count == 0)
            return;

        // Sum what all children contribute, then propagate once up the ancestor
        // chain — instead of walking ancestors separately for each child (which is
        // O(children × depth)). This is O(children + depth).
        long totalSize = 0, totalFiles = 0, totalDirs = 0;
        foreach (var child in _children)
        {
            totalSize += child.SizeBytes;
            totalFiles += child.FileCount;
            totalDirs += child.DirectoryCount + (child.IsDirectory ? 1 : 0);
            child.Parent = null;
        }
        _children.Clear();

        for (var a = this; a is not null; a = a.Parent)
        {
            a.SizeBytes -= totalSize;
            a.FileCount -= totalFiles;
            a.DirectoryCount -= totalDirs;
            if (a.SizeBytes < 0) a.SizeBytes = 0;
            if (a.FileCount < 0) a.FileCount = 0;
            if (a.DirectoryCount < 0) a.DirectoryCount = 0;
        }
    }
}
