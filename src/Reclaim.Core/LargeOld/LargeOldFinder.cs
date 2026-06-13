using Reclaim.Core.Scanning;

namespace Reclaim.Core.LargeOld;

/// <summary>One file surfaced by the large-and-old finder.</summary>
public sealed class LargeOldFile
{
    public required FileSystemNode Node { get; init; }
    public long SizeBytes => Node.SizeBytes;
    public DateTime LastWriteUtc => Node.LastWriteUtc;

    /// <summary>Whole days since last modification, relative to the scan time.</summary>
    public int AgeDays { get; init; }
}

public sealed class LargeOldReport
{
    public required IReadOnlyList<LargeOldFile> Files { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Finds files that are both large (≥ a size threshold) and old (not modified in
/// the last N days). Pure computation over the already-scanned tree — no extra
/// disk reads, no maintained data. Complements the duplicate finder: duplicates
/// catch redundant copies, this catches forgotten heavy files worth reviewing.
/// </summary>
public sealed class LargeOldFinder
{
    /// <summary>Minimum file size to be considered "large". Default 100 MB.</summary>
    public long MinSizeBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Minimum age in days (since last modified) to be considered "old".
    /// Default ~180 days (about six months).</summary>
    public int MinAgeDays { get; init; } = 180;

    /// <summary>Cap on returned files so a huge tree can't overwhelm the UI.</summary>
    public int MaxResults { get; init; } = 500;

    /// <param name="nowUtc">The reference "now" for age calculation; injectable so
    /// the logic is deterministic in tests.</param>
    public LargeOldReport Find(FileSystemNode root, DateTime nowUtc)
    {
        var matches = new List<LargeOldFile>();
        Collect(root, nowUtc, matches);

        // Largest first — the most worthwhile to review at the top.
        matches.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        var capped = matches.Count > MaxResults ? matches.GetRange(0, MaxResults) : matches;
        return new LargeOldReport
        {
            Files = capped,
            TotalBytes = capped.Sum(f => f.SizeBytes),
        };
    }

    private void Collect(FileSystemNode node, DateTime nowUtc, List<LargeOldFile> into)
    {
        if (!node.IsDirectory)
        {
            if (node.SizeBytes < MinSizeBytes)
                return;

            // Unknown timestamps (MinValue) are treated as not-old, so we never
            // mislabel a file whose date we couldn't read.
            if (node.LastWriteUtc == default)
                return;

            var ageDays = (int)(nowUtc - node.LastWriteUtc).TotalDays;
            if (ageDays < MinAgeDays)
                return;

            into.Add(new LargeOldFile { Node = node, AgeDays = ageDays });
            return;
        }

        foreach (var child in node.Children)
            Collect(child, nowUtc, into);
    }
}
