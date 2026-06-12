namespace Reclaim.Core.Scanning;

public enum FlatListKind
{
    Files,
    Folders,
}

/// <summary>
/// Flattens a scanned tree into a flat, size-sorted list of the largest items.
/// Files mode returns leaf files only; Folders mode returns directories only
/// (each folder's size is its full recursive total, so a parent will rank above
/// its children). Kept in Core so it can be unit tested without any UI.
/// </summary>
public static class FlatList
{
    /// <summary>
    /// Walks the subtree under <paramref name="root"/> and returns up to
    /// <paramref name="limit"/> items of the requested kind, largest first.
    /// The root itself is never included in Folders mode.
    /// </summary>
    public static IReadOnlyList<FileSystemNode> Largest(
        FileSystemNode root, FlatListKind kind, int limit = 1000)
    {
        var matches = new List<FileSystemNode>();
        Collect(root, kind, isRoot: true, matches);

        matches.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        if (matches.Count > limit)
            matches.RemoveRange(limit, matches.Count - limit);

        return matches;
    }

    private static void Collect(
        FileSystemNode node, FlatListKind kind, bool isRoot, List<FileSystemNode> sink)
    {
        if (node.IsDirectory)
        {
            if (kind == FlatListKind.Folders && !isRoot && node.SizeBytes > 0)
                sink.Add(node);

            foreach (var child in node.Children)
                Collect(child, kind, isRoot: false, sink);
        }
        else if (kind == FlatListKind.Files && node.SizeBytes > 0)
        {
            sink.Add(node);
        }
    }
}
