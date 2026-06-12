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

        // Snapshot, since RemoveFromTree mutates the list.
        foreach (var child in _children.ToList())
            child.RemoveFromTree();
    }
}
