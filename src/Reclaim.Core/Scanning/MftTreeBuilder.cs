namespace Reclaim.Core.Scanning;

/// <summary>
/// One file/directory record harvested from the NTFS MFT (via FSCTL_ENUM_USN_DATA).
/// This is a plain data carrier with no I/O, so the tree-reconstruction logic that
/// consumes it can be unit-tested with synthetic records — which matters because
/// path reconstruction is the trickiest, most failure-prone part of MFT scanning.
/// </summary>
public readonly record struct MftRecord(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    string Name,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastWriteUtc);

/// <summary>
/// Rebuilds a <see cref="FileSystemNode"/> tree from a flat list of MFT records.
///
/// The MFT gives every file/directory a unique File Reference Number (FRN) and the
/// FRN of its parent directory — but NOT full paths. So the tree must be stitched:
/// create a node per record, then link each to its parent by FRN. This class does
/// exactly that, in pure managed code with no OS calls, so it is fully testable.
///
/// The raw enumeration that produces the records lives in the Windows app project
/// (it needs P/Invoke and an elevated volume handle); this part is platform-agnostic
/// on purpose.
/// </summary>
public static class MftTreeBuilder
{
    /// <summary>
    /// Build a tree rooted at the volume root. <paramref name="rootFrn"/> is the FRN
    /// of the volume's root directory (5 on NTFS). <paramref name="driveLetter"/> like
    /// "C:" is used to form full paths.
    ///
    /// Records whose parent is missing (orphans) are attached to the root so nothing
    /// is silently lost. Sizes and counts are rolled up to ancestors afterward.
    /// </summary>
    public static FileSystemNode Build(
        IReadOnlyList<MftRecord> records, ulong rootFrn, string driveLetter)
    {
        var rootPath = driveLetter.EndsWith('\\') ? driveLetter : driveLetter + "\\";

        // Index children by their parent FRN so we can walk top-down. Skip NTFS
        // metafiles ($MFT, $LogFile, …) and self/parent entries.
        var childrenByParent = new Dictionary<ulong, List<MftRecord>>();
        foreach (var rec in records)
        {
            if (rec.FileReferenceNumber == rootFrn)
                continue;
            if (rec.Name is "." or ".." || rec.Name.StartsWith('$'))
                continue;
            if (!childrenByParent.TryGetValue(rec.ParentFileReferenceNumber, out var list))
                childrenByParent[rec.ParentFileReferenceNumber] = list = [];
            list.Add(rec);
        }

        var root = new FileSystemNode
        {
            Name = rootPath,
            FullPath = rootPath,
            IsDirectory = true,
        };

        // Build the tree top-down from the root FRN, so each node's parent path is
        // known when the node is constructed (FullPath is init-only).
        var visited = new HashSet<ulong> { rootFrn };
        AttachChildren(root, rootFrn, childrenByParent, visited);

        // Orphans: any record whose parent FRN was never seen as a node. Attach them
        // to the root so nothing is silently dropped.
        var seen = visited;
        foreach (var rec in records)
        {
            if (seen.Contains(rec.FileReferenceNumber))
                continue;
            if (rec.Name is "." or ".." || rec.Name.StartsWith('$'))
                continue;
            // Only attach if its parent truly isn't in the tree (genuine orphan).
            if (seen.Contains(rec.ParentFileReferenceNumber))
                continue;
            AttachNode(root, rec, childrenByParent, seen);
        }

        Rollup(root);
        return root;
    }

    private static void AttachChildren(
        FileSystemNode parent, ulong parentFrn,
        Dictionary<ulong, List<MftRecord>> childrenByParent, HashSet<ulong> visited)
    {
        if (!childrenByParent.TryGetValue(parentFrn, out var kids))
            return;
        foreach (var rec in kids)
        {
            if (!visited.Add(rec.FileReferenceNumber))
                continue; // guard against cycles / hardlink loops
            AttachNode(parent, rec, childrenByParent, visited);
        }
    }

    private static void AttachNode(
        FileSystemNode parent, MftRecord rec,
        Dictionary<ulong, List<MftRecord>> childrenByParent, HashSet<ulong> visited)
    {
        var path = parent.FullPath.EndsWith('\\')
            ? parent.FullPath + rec.Name
            : parent.FullPath + "\\" + rec.Name;

        var node = new FileSystemNode
        {
            Name = rec.Name,
            FullPath = path,
            IsDirectory = rec.IsDirectory,
            SizeBytes = rec.IsDirectory ? 0 : rec.SizeBytes,
            LastWriteUtc = rec.IsDirectory ? default : rec.LastWriteUtc,
            Parent = parent,
        };
        parent._children.Add(node);
        visited.Add(rec.FileReferenceNumber);

        if (rec.IsDirectory)
            AttachChildren(node, rec.FileReferenceNumber, childrenByParent, visited);
    }    /// <summary>Post-order roll-up of sizes and counts. Returns (size, files, dirs).</summary>
    private static (long size, long files, long dirs) Rollup(FileSystemNode node)
    {
        long size = node.IsDirectory ? 0 : node.SizeBytes;
        long files = node.IsDirectory ? 0 : 1;
        long dirs = 0;

        foreach (var child in node._children)
        {
            var (cs, cf, cd) = Rollup(child);
            size += cs;
            files += cf;
            dirs += cd + (child.IsDirectory ? 1 : 0);
        }

        if (node.IsDirectory)
            node.SizeBytes = size;
        node.FileCount = files;
        node.DirectoryCount = dirs;
        return (size, files, dirs);
    }
}
