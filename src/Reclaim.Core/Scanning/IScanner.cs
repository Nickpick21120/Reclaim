namespace Reclaim.Core.Scanning;

/// <summary>
/// A filesystem scanner. v0.1 ships <see cref="DirectoryScanner"/> (works everywhere);
/// v0.2 adds an NTFS MFT scanner behind this same interface for fast full-volume scans.
/// </summary>
public interface IScanner
{
    Task<ScanResult> ScanAsync(
        string rootPath,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
