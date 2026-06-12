namespace Reclaim.Core.Scanning;

public sealed class ScanOptions
{
    /// <summary>
    /// Directory levels (from the root) at which subdirectories are scanned in parallel.
    /// Below this depth scanning proceeds sequentially within the worker.
    /// Parallelizing only the shallow levels captures nearly all of the win
    /// (top-level folders dominate the work) without spawning thousands of tasks.
    /// </summary>
    public int ParallelDepth { get; init; } = 3;

    /// <summary>
    /// Skip reparse points (junctions, symlinks, OneDrive placeholders).
    /// Prevents cycles and double-counting. Strongly recommended on Windows.
    /// </summary>
    public bool SkipReparsePoints { get; init; } = true;

    /// <summary>Minimum interval between progress callbacks.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

public readonly record struct ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long BytesSeen,
    long ErrorCount,
    string CurrentPath);

public sealed class ScanResult
{
    public required FileSystemNode Root { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required long FilesScanned { get; init; }
    public required long DirectoriesScanned { get; init; }
    public required long ErrorCount { get; init; }
}
