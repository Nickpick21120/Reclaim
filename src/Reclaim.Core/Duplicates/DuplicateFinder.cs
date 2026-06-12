using Reclaim.Core.Scanning;

namespace Reclaim.Core.Duplicates;

/// <summary>A set of files with identical content.</summary>
public sealed class DuplicateGroup
{
    public required IReadOnlyList<FileSystemNode> Files { get; init; }

    /// <summary>Size of each file in the group (all identical).</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>Space that could be reclaimed by keeping one copy and removing
    /// the rest: (count - 1) * fileSize.</summary>
    public long ReclaimableBytes => FileSizeBytes * (Files.Count - 1);
}

public sealed class DuplicateReport
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }

    /// <summary>Total space reclaimable across all groups by keeping one of each.</summary>
    public long TotalReclaimableBytes { get; init; }

    /// <summary>How many files were hashed (i.e. shared a size with another).</summary>
    public int FilesHashed { get; init; }
}

/// <summary>Computes a content hash for a file. Injected so the detector can be
/// tested without real disk I/O.</summary>
public interface IFileHasher
{
    /// <summary>A stable content hash (e.g. SHA-256 hex). Implementations may
    /// throw on unreadable files; the detector handles that gracefully.</summary>
    string Hash(string fullPath);
}

/// <summary>
/// Finds files with identical content. Uses a size-first strategy: files whose
/// size is unique cannot be duplicates and are never hashed, so only genuine
/// candidates (same-size files) get read. This makes scans fast even on large
/// trees. All logic is pure given an <see cref="IFileHasher"/>; no maintained
/// data, just computation over the user's own files.
/// </summary>
public sealed class DuplicateFinder
{
    private readonly IFileHasher _hasher;

    /// <summary>Files at or below this size are ignored (tiny files rarely
    /// matter and create noise). Default 1 KB.</summary>
    public long MinFileSizeBytes { get; init; } = 1024;

    public DuplicateFinder(IFileHasher hasher) => _hasher = hasher;

    /// <param name="progress">Optional reporter of (filesHashed, totalToHash) so
    /// the UI can show a real percentage through the slow hashing phase. The total
    /// is known only after the size-grouping pass, so callers may show a brief
    /// indeterminate "analyzing" state until the first report arrives.</param>
    public DuplicateReport Find(FileSystemNode root, IProgress<(int done, int total)>? progress = null)
    {
        // Collect all files at or above the threshold.
        var files = new List<FileSystemNode>();
        Collect(root, files);

        // 1) Group by size. Only sizes shared by 2+ files can contain duplicates.
        var bySize = files
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        // How many files will actually be hashed (the slow part) — lets the UI
        // show a true percentage rather than an unbounded spinner.
        var totalToHash = bySize.Sum(g => g.Count());
        progress?.Report((0, totalToHash));

        var groups = new List<DuplicateGroup>();
        var hashed = 0;
        var processed = 0; // includes skips, for progress only

        foreach (var sizeGroup in bySize)
        {
            // 2) Within a size, hash each file and group by hash.
            var byHash = new Dictionary<string, List<FileSystemNode>>();
            foreach (var file in sizeGroup)
            {
                string hash;
                try
                {
                    hash = _hasher.Hash(file.FullPath);
                    hashed++;
                    processed++;
                    progress?.Report((processed, totalToHash));
                }
                catch
                {
                    // Unreadable file: skip it rather than failing the whole scan,
                    // but still advance progress so the bar reaches 100%.
                    processed++;
                    progress?.Report((processed, totalToHash));
                    continue;
                }

                if (!byHash.TryGetValue(hash, out var list))
                    byHash[hash] = list = [];
                list.Add(file);
            }

            // 3) Any hash shared by 2+ files is a true duplicate set.
            foreach (var hashGroup in byHash.Values.Where(v => v.Count > 1))
            {
                groups.Add(new DuplicateGroup
                {
                    Files = hashGroup,
                    FileSizeBytes = hashGroup[0].SizeBytes,
                });
            }
        }

        // Largest reclaimable groups first — most impactful at the top.
        groups.Sort((a, b) => b.ReclaimableBytes.CompareTo(a.ReclaimableBytes));

        return new DuplicateReport
        {
            Groups = groups,
            TotalReclaimableBytes = groups.Sum(g => g.ReclaimableBytes),
            FilesHashed = hashed,
        };
    }

    private void Collect(FileSystemNode node, List<FileSystemNode> into)
    {
        if (!node.IsDirectory)
        {
            if (node.SizeBytes >= MinFileSizeBytes)
                into.Add(node);
            return;
        }
        foreach (var child in node.Children)
            Collect(child, into);
    }
}
