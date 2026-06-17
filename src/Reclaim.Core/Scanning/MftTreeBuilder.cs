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
    DateTime LastWriteUtc,
    ulong BaseRecordFrn = 0,
    // Namespace byte of the chosen $FILE_NAME (used by the tree builder to promote
    // a Win32 long name over a DOS 8.3 short name across base/extension records).
    byte ChosenNamespace = 255);

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

        // First pass over EXTENSION records (BaseRecordFrn != 0). These are not files
        // themselves — they hold attributes that overflowed from a base record via
        // $ATTRIBUTE_LIST: the real $DATA size, and sometimes the Win32 long
        // $FILE_NAME and timestamps. We collect, per base FRN:
        //   - the largest $DATA real size (a single value, never summed)
        //   - a promoted long name (non-DOS) if the base only had a short name
        //   - a last-write timestamp if the extension carries one
        var extSizeByBase = new Dictionary<ulong, long>();
        var extNameByBase = new Dictionary<ulong, (string name, byte ns)>();
        var extWriteByBase = new Dictionary<ulong, DateTime>();
        foreach (var rec in records)
        {
            if (rec.BaseRecordFrn == 0)
                continue; // not an extension record

            if (rec.SizeBytes > 0)
            {
                extSizeByBase.TryGetValue(rec.BaseRecordFrn, out var cur);
                if (rec.SizeBytes > cur)
                    extSizeByBase[rec.BaseRecordFrn] = rec.SizeBytes;
            }
            if (!string.IsNullOrEmpty(rec.Name))
            {
                // Keep the best (lowest namespace rank) name seen for this base.
                if (!extNameByBase.TryGetValue(rec.BaseRecordFrn, out var existing)
                    || NamespaceRank(rec.ChosenNamespace) < NamespaceRank(existing.ns))
                    extNameByBase[rec.BaseRecordFrn] = (rec.Name, rec.ChosenNamespace);
            }
            if (rec.LastWriteUtc != default)
                extWriteByBase[rec.BaseRecordFrn] = rec.LastWriteUtc;
        }

        // Index children by their parent FRN so we can walk top-down. Skip NTFS
        // metafiles, self/parent entries, AND extension records (BaseRecordFrn != 0).
        // For each base file, reconcile in the better size/name/timestamp that may
        // have lived in its extension records.
        var childrenByParent = new Dictionary<ulong, List<MftRecord>>();
        foreach (var rec in records)
        {
            if (rec.FileReferenceNumber == rootFrn)
                continue;
            if (rec.BaseRecordFrn != 0)
                continue; // extension record — reconciled into its base, not a node
            if (string.IsNullOrEmpty(rec.Name))
                continue; // base record with no usable name
            if (rec.Name is "." or ".." || rec.Name.StartsWith('$'))
                continue;

            var effective = rec;

            // Size: max of base's own $DATA and any extension's $DATA (never summed).
            if (!rec.IsDirectory && extSizeByBase.TryGetValue(rec.FileReferenceNumber, out var extSize)
                && extSize > effective.SizeBytes)
                effective = effective with { SizeBytes = extSize };

            // Name: if the base record's chosen name is a DOS 8.3 name (namespace 2)
            // but an extension carries a better (Win32/POSIX) long name, promote it.
            if (rec.ChosenNamespace == 2
                && extNameByBase.TryGetValue(rec.FileReferenceNumber, out var promoted)
                && NamespaceRank(promoted.ns) < 2
                && promoted.name.Length > 0)
                effective = effective with { Name = promoted.name };

            // Timestamp: if the base lacked one but an extension had it.
            if (!rec.IsDirectory && effective.LastWriteUtc == default
                && extWriteByBase.TryGetValue(rec.FileReferenceNumber, out var extWrite))
                effective = effective with { LastWriteUtc = extWrite };

            if (!childrenByParent.TryGetValue(effective.ParentFileReferenceNumber, out var list))
                childrenByParent[effective.ParentFileReferenceNumber] = list = [];
            list.Add(effective);
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
            if (rec.BaseRecordFrn != 0)
                continue; // extension record, not a file
            if (string.IsNullOrEmpty(rec.Name))
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

    /// <summary>Namespace preference rank (lower = better); DOS 8.3 is worst.
    /// Mirrors the parser's ranking so name promotion across records is consistent.</summary>
    private static int NamespaceRank(byte ns) => ns switch
    {
        1 => 0, // Win32
        3 => 0, // Win32 + DOS combined
        0 => 1, // POSIX
        2 => 2, // DOS 8.3
        _ => 3,
    };

    private static void AttachChildren(
        FileSystemNode parent, ulong parentFrn,
        Dictionary<ulong, List<MftRecord>> childrenByParent, HashSet<ulong> visited)
    {        if (!childrenByParent.TryGetValue(parentFrn, out var kids))
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
