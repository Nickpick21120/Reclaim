namespace Reclaim.Core.Scanning;

/// <summary>One file whose size differs between two scans.</summary>
public readonly record struct SizeDiscrepancy(
    string Path,
    long SizeA,
    long SizeB)
{
    /// <summary>Absolute difference; the magnitude of the disagreement.</summary>
    public long Delta => Math.Abs(SizeA - SizeB);
}

/// <summary>Summary of how two scan trees differ, for diagnosing scanner accuracy.</summary>
public sealed class ScanComparison
{
    public required long TotalA { get; init; }
    public required long TotalB { get; init; }
    public required int FilesOnlyInA { get; init; }
    public required int FilesOnlyInB { get; init; }
    public required IReadOnlyList<SizeDiscrepancy> Discrepancies { get; init; }
    public required long DiscrepancyTotal { get; init; }

    public long TotalDelta => Math.Abs(TotalA - TotalB);
}

/// <summary>
/// Compares two scanned trees file-by-file to locate where their sizes diverge.
/// Pure logic (no I/O), so it's unit-testable; used to diagnose the MFT scanner's
/// accuracy against the trusted directory walker. The biggest discrepancies are
/// surfaced first because they explain most of any total mismatch.
/// </summary>
public static class ScanComparer
{
    /// <param name="maxDiscrepancies">Cap on listed differences (largest first).</param>
    public static ScanComparison Compare(
        FileSystemNode treeA, FileSystemNode treeB, int maxDiscrepancies = 200)
    {
        var filesA = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var filesB = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        CollectFiles(treeA, filesA);
        CollectFiles(treeB, filesB);

        var discrepancies = new List<SizeDiscrepancy>();
        var onlyInA = 0;
        var onlyInB = 0;

        foreach (var (path, sizeA) in filesA)
        {
            if (filesB.TryGetValue(path, out var sizeB))
            {
                if (sizeA != sizeB)
                    discrepancies.Add(new SizeDiscrepancy(path, sizeA, sizeB));
            }
            else
            {
                onlyInA++;
                discrepancies.Add(new SizeDiscrepancy(path, sizeA, 0));
            }
        }
        foreach (var (path, sizeB) in filesB)
        {
            if (!filesA.ContainsKey(path))
            {
                onlyInB++;
                discrepancies.Add(new SizeDiscrepancy(path, 0, sizeB));
            }
        }

        discrepancies.Sort((x, y) => y.Delta.CompareTo(x.Delta));
        var discrepancyTotal = discrepancies.Sum(d => d.Delta);
        var capped = discrepancies.Count > maxDiscrepancies
            ? discrepancies.GetRange(0, maxDiscrepancies)
            : discrepancies;

        return new ScanComparison
        {
            TotalA = SumFiles(filesA),
            TotalB = SumFiles(filesB),
            FilesOnlyInA = onlyInA,
            FilesOnlyInB = onlyInB,
            Discrepancies = capped,
            DiscrepancyTotal = discrepancyTotal,
        };
    }

    private static void CollectFiles(FileSystemNode node, Dictionary<string, long> into)
    {
        if (!node.IsDirectory)
        {
            into[node.FullPath] = node.SizeBytes;
            return;
        }
        foreach (var child in node.Children)
            CollectFiles(child, into);
    }

    private static long SumFiles(Dictionary<string, long> files)
    {
        long total = 0;
        foreach (var v in files.Values)
            total += v;
        return total;
    }
}
