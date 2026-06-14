using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Reclaim.Core.Scanning;

namespace Reclaim.App.Services;

/// <summary>
/// EXPERIMENTAL fast scanner that reads the NTFS Master File Table directly from
/// the raw volume, giving WizTree-class whole-volume speed WITH file sizes and
/// timestamps (unlike the FSCTL_ENUM_USN_DATA approach, which omits sizes).
///
/// Pipeline (each pure step is unit-tested in Reclaim.Core):
///   1. Read boot sector            -> NtfsBootSectorParser  (volume geometry)
///   2. Read MFT record 0 ($MFT)    -> NtfsRecordParser      (its own $DATA runs)
///   3. Decode $MFT data runs       -> NtfsDataRunDecoder    (where the MFT lives)
///   4. Read every MFT record       -> NtfsRecordParser      (one MftRecord each)
///   5. Stitch records into a tree  -> MftTreeBuilder        (paths + rollup)
///
/// ONLY the raw-disk reads in this file are untestable off real hardware; all the
/// parsing/structure logic is covered by sandbox unit tests. Requires admin + NTFS.
/// </summary>
public sealed class MftScanner : IScanner
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    /// <summary>Pre-flight: only an NTFS fixed drive, scanned at its root, while
    /// elevated, can use MFT scanning. Callers fall back to DirectoryScanner otherwise.</summary>
    public static bool CanScan(string rootPath, out string reason)
    {
        reason = "";
        try
        {
            var full = Path.GetFullPath(rootPath);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !root.Contains(':'))
            {
                reason = "Not a local drive path.";
                return false;
            }
            // MFT scanning is whole-volume; only meaningful when scanning the drive root.
            if (!string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                reason = "MFT scanning covers a whole drive; for a subfolder the normal scanner is used.";
                return false;
            }
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                reason = "MFT scanning works only on fixed local drives.";
                return false;
            }
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Drive is {drive.DriveFormat}, not NTFS.";
                return false;
            }
            if (!IsElevated())
            {
                reason = "MFT scanning requires administrator privileges.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public Task<ScanResult> ScanAsync(
        string rootPath, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => ScanCore(rootPath, progress, cancellationToken), cancellationToken);

    private ScanResult ScanCore(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(rootPath))!; // "C:\"
        var driveLetter = driveRoot.TrimEnd('\\');                     // "C:"
        var volumePath = $@"\\.\{driveLetter}";                        // "\\.\C:"

        using var handle = CreateFileW(
            volumePath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException(
                $"Couldn't open volume {volumePath} (error {Marshal.GetLastWin32Error()}). " +
                "MFT scanning needs administrator rights.");

        using var volume = new FileStream(handle, FileAccess.Read);

        // 1) Boot sector → geometry.
        var bootBuf = ReadAt(volume, 0, 512);
        var geo = NtfsBootSectorParser.Parse(bootBuf)
                  ?? throw new IOException("Not an NTFS volume (boot sector unrecognized).");

        // 2) Read MFT record 0 (the $MFT's own record) to learn where the MFT lives.
        var recordSize = geo.BytesPerMftRecord;
        var mftRecord0 = ReadAt(volume, geo.MftByteOffset, recordSize);

        // 3) Decode $MFT's $DATA runs. We need the raw run list bytes from record 0;
        //    extract them via a focused helper (the $DATA non-resident run header).
        var runs = ExtractMftDataRuns(mftRecord0, geo)
                   ?? throw new IOException("Couldn't locate the MFT data runs.");

        // 4) Walk every run, reading records in chunks, parsing each.
        var records = new List<MftRecord>(200_000);
        long recordIndex = 0;
        long filesSeen = 0;
        var clusterBytes = geo.BytesPerCluster;

        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();
            if (run.StartCluster < 0)
            {
                // sparse run: advance the record index but nothing to read.
                recordIndex += run.ClusterCount * clusterBytes / recordSize;
                continue;
            }

            long runByteStart = run.StartCluster * clusterBytes;
            long runByteLen = run.ClusterCount * clusterBytes;

            // Read the run in ~1 MB chunks to bound memory.
            const int chunk = 1024 * 1024;
            long read = 0;
            while (read < runByteLen)
            {
                ct.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(chunk, runByteLen - read);
                // align to a whole number of records
                toRead -= toRead % recordSize;
                if (toRead <= 0) break;

                var buf = ReadAt(volume, runByteStart + read, toRead);
                for (var o = 0; o + recordSize <= buf.Length; o += recordSize)
                {
                    var single = new byte[recordSize];
                    Array.Copy(buf, o, single, 0, recordSize);
                    var parsed = NtfsRecordParser.Parse(single, geo.BytesPerSector, (ulong)recordIndex);
                    recordIndex++;
                    if (parsed is { } r)
                    {
                        records.Add(r);
                        filesSeen++;
                    }
                }
                read += toRead;
                progress?.Report(new ScanProgress(filesSeen, 0, 0, 0, volumePath));
            }
        }

        // 5) Stitch into a tree. NTFS root directory FRN is always 5.
        var tree = MftTreeBuilder.Build(records, rootFrn: 5, driveLetter);

        sw.Stop();
        return new ScanResult
        {
            Root = tree,
            Elapsed = sw.Elapsed,
            FilesScanned = tree.FileCount,
            DirectoriesScanned = tree.DirectoryCount,
            ErrorCount = 0,
        };
    }

    /// <summary>Read exactly <paramref name="count"/> bytes at an absolute volume
    /// offset. Raw volume reads must be sector-aligned, which our offsets are
    /// (boot sector at 0, MFT at a cluster boundary, records sized in sectors).</summary>
    private static byte[] ReadAt(FileStream volume, long offset, int count)
    {
        volume.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = volume.Read(buf, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        return buf;
    }

    /// <summary>Find the $DATA attribute (0x80) in MFT record 0 and decode its run
    /// list. Applies the fixup first (reusing the parser's validation by parsing,
    /// but we need the raw run bytes, so we walk attributes here directly).</summary>
    private static List<DataRun>? ExtractMftDataRuns(byte[] record0, NtfsVolumeGeometry geo)
    {
        // Validate + fixup via the tested parser path is not enough (it doesn't expose
        // run bytes), so walk attributes to find $DATA's run list offset. The record
        // has already had no fixup applied here; apply a minimal fixup inline.
        if (record0.Length < 0x30) return null;
        if (record0[0] != (byte)'F' || record0[1] != (byte)'I' ||
            record0[2] != (byte)'L' || record0[3] != (byte)'E') return null;

        // Fixup (mirror of NtfsRecordParser.ApplyFixup; kept local since we need raw bytes).
        int usaOffset = BitConverter.ToUInt16(record0, 0x04);
        int usaCount = BitConverter.ToUInt16(record0, 0x06);
        if (usaCount > 0 && usaOffset + usaCount * 2 <= record0.Length)
        {
            for (var i = 1; i < usaCount; i++)
            {
                var sectorEnd = i * geo.BytesPerSector - 2;
                if (sectorEnd + 2 > record0.Length) break;
                record0[sectorEnd] = record0[usaOffset + i * 2];
                record0[sectorEnd + 1] = record0[usaOffset + i * 2 + 1];
            }
        }

        int attrOffset = BitConverter.ToUInt16(record0, 0x14);
        while (attrOffset + 8 <= record0.Length)
        {
            var type = BitConverter.ToUInt32(record0, attrOffset);
            if (type == 0xFFFFFFFF) break;
            var attrLength = BitConverter.ToInt32(record0, attrOffset + 4);
            if (attrLength <= 0 || attrOffset + attrLength > record0.Length) break;

            if (type == 0x80) // $DATA
            {
                var nonResident = record0[attrOffset + 0x08] != 0;
                if (!nonResident) return null; // $MFT data is always non-resident
                // Non-resident header: run list offset is at +0x20 (2 bytes).
                int runListOffset = BitConverter.ToUInt16(record0, attrOffset + 0x20);
                return NtfsDataRunDecoder.Decode(record0, attrOffset + runListOffset);
            }
            attrOffset += attrLength;
        }
        return null;
    }
}
