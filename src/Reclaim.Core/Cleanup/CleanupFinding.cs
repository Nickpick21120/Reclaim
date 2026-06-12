using Reclaim.Core.Scanning;

namespace Reclaim.Core.Cleanup;

/// <summary>
/// A concrete reclaimable location found during analysis: one rule matched to
/// one directory in the scan, with its current size. Purely descriptive — it
/// represents space that COULD be reclaimed, not an instruction to delete.
/// </summary>
public sealed class CleanupFinding
{
    public required CleanupRule Rule { get; init; }
    public required FileSystemNode Node { get; init; }

    public string Path => Node.FullPath;
    public long SizeBytes => Node.SizeBytes;
    public long FileCount => Node.FileCount;

    public CleanupCategory Category => Rule.Category;
    public SafetyTier Safety => Rule.Safety;
}

/// <summary>One category's worth of findings, aggregated for the report.</summary>
public sealed class CleanupCategorySummary
{
    public required CleanupCategory Category { get; init; }
    public required IReadOnlyList<CleanupFinding> Findings { get; init; }

    public long TotalBytes { get; init; }
    public int LocationCount => Findings.Count;

    /// <summary>The most cautious tier among this category's findings, so the UI
    /// can flag a whole category that contains anything needing care.</summary>
    public SafetyTier HighestRisk { get; init; }
}

public sealed class CleanupReport
{
    public required IReadOnlyList<CleanupCategorySummary> Categories { get; init; }
    public required IReadOnlyList<CleanupFinding> AllFindings { get; init; }

    /// <summary>Total reclaimable space across every finding.</summary>
    public long TotalReclaimableBytes { get; init; }

    /// <summary>Reclaimable space limited to the two safest tiers — the figure
    /// it's responsible to highlight as "safely reclaimable".</summary>
    public long SafelyReclaimableBytes { get; init; }
}
