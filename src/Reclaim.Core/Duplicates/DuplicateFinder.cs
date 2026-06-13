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

    /// <summary>How many files were fully hashed (i.e. survived the prefix filter).</summary>
    public int FilesHashed { get; init; }

    /// <summary>Total duplicate groups found before the display cap was applied.
    /// Equals Groups.Count unless the cap truncated the list.</summary>
    public int TotalGroupsFound { get; init; }
}

/// <summary>Computes content hashes for a file. Injected so the detector can be
/// tested without real disk I/O.</summary>
public interface IFileHasher
{
    /// <summary>A stable hash of the FULL file content (e.g. SHA-256 hex).
    /// Implementations may throw on unreadable files; the detector handles that.</summary>
    string Hash(string fullPath);

    /// <summary>A stable hash of only the first <paramref name="maxBytes"/> bytes.
    /// Used as a cheap pre-filter so most non-duplicates are ruled out without
    /// reading entire files. Implementations may throw on unreadable files.</summary>
    string HashPrefix(string fullPath, int maxBytes);
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

    /// <summary>Bytes read for the cheap prefix pre-filter. Two files that differ
    /// usually differ within the first few KB, so this rules out most candidates
    /// without reading them whole.</summary>
    public int PrefixBytes { get; init; } = 4096;

    /// <summary>Cap on the number of duplicate groups returned, so a pathological
    /// tree can't produce a result set huge enough to overwhelm the UI. The
    /// largest-reclaimable groups are kept.</summary>
    public int MaxResultGroups { get; init; } = 1000;

    public DuplicateFinder(IFileHasher hasher) => _hasher = hasher;

    /// <param name="progress">Optional reporter of (filesHashed, totalToHash) so
    /// the UI can show a real percentage through the slow hashing phase. The total
    /// is known only after the size-grouping pass, so callers may show a brief
    /// indeterminate "analyzing" state until the first report arrives.</param>
    /// <param name="cancellationToken">Cancels the scan cooperatively; the hashing
    /// loops check it and throw OperationCanceledException promptly when requested.</param>
    public DuplicateReport Find(FileSystemNode root, IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Collect all files at or above the threshold.
        var files = new List<FileSystemNode>();
        Collect(root, files);

        // 1) Group by size. Only sizes shared by 2+ files can contain duplicates.
        var bySize = files
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        // Progress total = candidates that will be prefix-checked (the first read
        // pass). The full-hash pass touches only a subset, so we treat the prefix
        // pass as the progress denominator — it's the bulk of the file-touching.
        var totalCandidates = bySize.Sum(g => g.Count());
        progress?.Report((0, totalCandidates));

        var groups = new List<DuplicateGroup>();
        var hashed = 0;
        var processed = 0;

        foreach (var sizeGroup in bySize)
        {
            // 2) CHEAP PRE-FILTER: hash only the first PrefixBytes of each file.
            // Files that differ early (the common case) are separated here without
            // ever reading them whole — this is what keeps the scan fast.
            var byPrefix = new Dictionary<string, List<FileSystemNode>>();
            foreach (var file in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                try
                {
                    var pre = _hasher.HashPrefix(file.FullPath, PrefixBytes);
                    if (!byPrefix.TryGetValue(pre, out var list))
                        byPrefix[pre] = list = [];
                    list.Add(file);
                }
                catch
                {
                    // Unreadable: skip.
                }
                progress?.Report((processed, totalCandidates));
            }

            // 3) FULL HASH only for files whose prefix collided (2+ sharing a
            // prefix AND size). For small files (<= PrefixBytes) the prefix already
            // covers the whole file, so we can trust it and skip re-reading.
            foreach (var prefixGroup in byPrefix.Values.Where(v => v.Count > 1))
            {
                var fileSize = prefixGroup[0].SizeBytes;
                if (fileSize <= PrefixBytes)
                {
                    // Prefix already hashed the entire file — it's a confirmed match.
                    groups.Add(new DuplicateGroup { Files = prefixGroup, FileSizeBytes = fileSize });
                    continue;
                }

                var byHash = new Dictionary<string, List<FileSystemNode>>();
                foreach (var file in prefixGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var hash = _hasher.Hash(file.FullPath);
                        hashed++;
                        if (!byHash.TryGetValue(hash, out var list))
                            byHash[hash] = list = [];
                        list.Add(file);
                    }
                    catch
                    {
                        // Unreadable: skip.
                    }
                }

                foreach (var hashGroup in byHash.Values.Where(v => v.Count > 1))
                    groups.Add(new DuplicateGroup { Files = hashGroup, FileSizeBytes = fileSize });
            }
        }

        // Largest reclaimable groups first, then cap the result set so the UI is
        // never asked to render a pathological number of rows.
        groups.Sort((a, b) => b.ReclaimableBytes.CompareTo(a.ReclaimableBytes));
        var capped = groups.Count > MaxResultGroups
            ? groups.GetRange(0, MaxResultGroups)
            : groups;

        return new DuplicateReport
        {
            Groups = capped,
            TotalReclaimableBytes = capped.Sum(g => g.ReclaimableBytes),
            FilesHashed = hashed,
            TotalGroupsFound = groups.Count,
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
