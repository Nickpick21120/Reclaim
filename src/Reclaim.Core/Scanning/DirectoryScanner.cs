using System.IO.Enumeration;

namespace Reclaim.Core.Scanning;

/// <summary>
/// Scans a directory tree by enumerating the filesystem. Works on any volume
/// (NTFS, FAT, exFAT, network shares). Parallelizes the shallow directory levels,
/// where the bulk of the fan-out lives, and reads file sizes straight from the
/// directory enumeration (no per-file stat call), which on Windows means one
/// FindFirstFile/FindNextFile pass per directory.
/// </summary>
public sealed class DirectoryScanner : IScanner
{
    // Snapshot of one directory entry. FileSystemEntry is a ref struct and
    // cannot be stored, so we project the few fields we need into this.
    private readonly record struct Entry(string Name, bool IsDirectory, long Size, FileAttributes Attributes);

    private sealed class Counters
    {
        public long Files;
        public long Directories;
        public long Bytes;
        public long Errors;
        public long LastReportTicks;
        public volatile string CurrentPath = "";
    }

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"Directory not found: {fullRoot}");

        var counters = new Counters();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var root = new FileSystemNode
        {
            Name = Path.GetFileName(fullRoot) is { Length: > 0 } n ? n : fullRoot,
            FullPath = fullRoot,
            IsDirectory = true,
        };

        await Task.Run(
            () => ScanDirectory(root, depth: 0, options, counters, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        started.Stop();

        // Final progress report so the UI lands on the true totals.
        progress?.Report(new ScanProgress(
            counters.Files, counters.Directories, counters.Bytes, counters.Errors, fullRoot));

        return new ScanResult
        {
            Root = root,
            Elapsed = started.Elapsed,
            FilesScanned = counters.Files,
            DirectoriesScanned = counters.Directories,
            ErrorCount = counters.Errors,
        };
    }

    private void ScanDirectory(
        FileSystemNode node,
        int depth,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        List<Entry> entries;
        try
        {
            entries = EnumerateEntries(node.FullPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            node.HadError = true;
            Interlocked.Increment(ref counters.Errors);
            return;
        }

        var children = new List<FileSystemNode>(entries.Count);
        var subDirs = new List<FileSystemNode>();

        foreach (var entry in entries)
        {
            if (options.SkipReparsePoints && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var child = new FileSystemNode
            {
                Name = entry.Name,
                FullPath = Path.Join(node.FullPath, entry.Name),
                IsDirectory = entry.IsDirectory,
                Parent = node,
            };

            if (entry.IsDirectory)
            {
                subDirs.Add(child);
            }
            else
            {
                child.SizeBytes = entry.Size;
                child.FileCount = 1;
                Interlocked.Increment(ref counters.Files);
                Interlocked.Add(ref counters.Bytes, entry.Size);
            }

            children.Add(child);
        }

        // Recurse into subdirectories — in parallel near the root, inline below that.
        if (subDirs.Count > 0)
        {
            if (depth < options.ParallelDepth && subDirs.Count > 1)
            {
                Parallel.ForEach(
                    subDirs,
                    new ParallelOptions { CancellationToken = ct },
                    sub => ScanDirectory(sub, depth + 1, options, counters, progress, ct));
            }
            else
            {
                foreach (var sub in subDirs)
                    ScanDirectory(sub, depth + 1, options, counters, progress, ct);
            }
        }

        // Aggregate child totals into this node.
        long size = 0, files = 0, dirs = 0;
        var hadError = node.HadError;
        foreach (var child in children)
        {
            size += child.SizeBytes;
            files += child.FileCount;
            if (child.IsDirectory)
            {
                dirs += 1 + child.DirectoryCount;
                hadError |= child.HadError;
            }
        }

        children.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        node._children = children;
        node.SizeBytes = size;
        node.FileCount = files;
        node.DirectoryCount = dirs;
        node.HadError = hadError;

        Interlocked.Increment(ref counters.Directories);
        MaybeReportProgress(node.FullPath, options, counters, progress);
    }

    private static List<Entry> EnumerateEntries(string path)
    {
        var enumerable = new FileSystemEnumerable<Entry>(
            path,
            static (ref FileSystemEntry entry) => new Entry(
                entry.FileName.ToString(),
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length,
                entry.Attributes),
            new EnumerationOptions
            {
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                AttributesToSkip = 0, // we decide what to skip, not the framework
            });

        return enumerable.ToList();
    }

    private static void MaybeReportProgress(
        string currentPath, ScanOptions options, Counters counters, IProgress<ScanProgress>? progress)
    {
        if (progress is null)
            return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref counters.LastReportTicks);
        if (now - last < options.ProgressInterval.TotalMilliseconds)
            return;

        // Only one thread wins the right to report for this interval.
        if (Interlocked.CompareExchange(ref counters.LastReportTicks, now, last) != last)
            return;

        counters.CurrentPath = currentPath;
        progress.Report(new ScanProgress(
            Interlocked.Read(ref counters.Files),
            Interlocked.Read(ref counters.Directories),
            Interlocked.Read(ref counters.Bytes),
            Interlocked.Read(ref counters.Errors),
            currentPath));
    }
}
