namespace Reclaim.Core.Scanning;

/// <summary>
/// The geometry of an NTFS volume, read from its boot sector. Everything needed
/// to locate and size the MFT on disk.
/// </summary>
public readonly record struct NtfsVolumeGeometry(
    int BytesPerSector,
    int SectorsPerCluster,
    long MftStartCluster,
    int BytesPerMftRecord)
{
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;
    public long MftByteOffset => MftStartCluster * BytesPerCluster;
}

/// <summary>
/// Parses the NTFS boot sector (the first 512 bytes of the volume) into a
/// <see cref="NtfsVolumeGeometry"/>. Pure byte parsing, so it is unit-testable
/// with a synthetic boot sector — the real disk read happens in the App layer.
///
/// Field offsets follow the documented NTFS BPB (BIOS Parameter Block) layout.
/// </summary>
public static class NtfsBootSectorParser
{
    public static NtfsVolumeGeometry? Parse(byte[] boot)
    {
        if (boot.Length < 0x54)
            return null;

        // Bytes 3..10 are the OEM id; for NTFS it reads "NTFS    ".
        if (boot[3] != (byte)'N' || boot[4] != (byte)'T' ||
            boot[5] != (byte)'F' || boot[6] != (byte)'S')
            return null;

        int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        int sectorsPerCluster = boot[0x0D];
        if (bytesPerSector <= 0 || sectorsPerCluster <= 0)
            return null;

        // MFT starting cluster number (logical cluster) at offset 0x30 (8 bytes).
        long mftStartCluster = BitConverter.ToInt64(boot, 0x30);

        // "Clusters per MFT record" at 0x40. If negative, it encodes a power of two:
        // the record size is 2^(-value) bytes (this is how NTFS expresses sub-cluster
        // record sizes, e.g. -10 => 1024 bytes). If positive, it's a cluster count.
        sbyte clustersPerRecordRaw = unchecked((sbyte)boot[0x40]);
        int bytesPerRecord = clustersPerRecordRaw < 0
            ? 1 << (-clustersPerRecordRaw)
            : clustersPerRecordRaw * bytesPerSector * sectorsPerCluster;

        if (bytesPerRecord <= 0)
            bytesPerRecord = 1024; // sane default; the on-disk header also states it

        return new NtfsVolumeGeometry(
            bytesPerSector, sectorsPerCluster, mftStartCluster, bytesPerRecord);
    }
}
