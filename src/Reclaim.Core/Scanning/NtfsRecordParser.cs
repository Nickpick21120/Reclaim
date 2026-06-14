namespace Reclaim.Core.Scanning;

/// <summary>
/// Parses a single raw NTFS MFT record (typically 1024 bytes) into an
/// <see cref="MftRecord"/>. PURE byte manipulation — no disk I/O — so it can be
/// unit-tested with synthetic records. This is the riskiest part of MFT scanning
/// (the binary layout, the fixup sequence, resident/non-resident sizing), which
/// is exactly why it lives here in testable isolation rather than tangled into
/// the raw-disk reader.
///
/// Layout references: the FILE record header, then a chain of attributes. We read
/// $STANDARD_INFORMATION (0x10) for timestamps, $FILE_NAME (0x30) for the name and
/// parent reference, and $DATA (0x80) for the authoritative file size.
/// </summary>
public static class NtfsRecordParser
{
    private const int SignatureOffset = 0x00;        // "FILE"
    private const int UpdateSeqOffset = 0x04;        // offset to update sequence array
    private const int UpdateSeqSizeOffset = 0x06;    // size (in words) of that array
    private const int FlagsOffset = 0x16;            // 0x01 in-use, 0x02 directory
    private const int FirstAttrOffset = 0x14;        // offset to first attribute

    private const uint AttrStandardInformation = 0x10;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;

    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;

    /// <summary>
    /// Parse one record. Returns null if the record isn't a valid in-use FILE record.
    /// <paramref name="ownFrn"/> is the record's own File Reference Number (its index
    /// in the MFT), which the caller knows from the record's position.
    /// </summary>
    public static MftRecord? Parse(byte[] record, int bytesPerSector, ulong ownFrn)
    {
        if (record.Length < 0x30)
            return null;

        // Must start with "FILE".
        if (record[0] != (byte)'F' || record[1] != (byte)'I' ||
            record[2] != (byte)'L' || record[3] != (byte)'E')
            return null;

        // Apply the fixup (update sequence) before reading anything past the first
        // sector — NTFS stuffs a check value into the last 2 bytes of each sector,
        // and the originals live in the update sequence array. Skip this and every
        // multi-sector record is silently corrupt.
        if (!ApplyFixup(record, bytesPerSector))
            return null;

        var flags = BitConverter.ToUInt16(record, FlagsOffset);
        if ((flags & FlagInUse) == 0)
            return null; // deleted record — ignore

        var isDir = (flags & FlagDirectory) != 0;

        string? name = null;
        ulong parentFrn = 0;
        long dataSize = 0;
        long fileNameSize = 0;
        DateTime lastWrite = default;

        int attrOffset = BitConverter.ToUInt16(record, FirstAttrOffset);
        while (attrOffset + 8 <= record.Length)
        {
            var type = BitConverter.ToUInt32(record, attrOffset);
            if (type == AttrEnd)
                break;

            var attrLength = BitConverter.ToInt32(record, attrOffset + 4);
            if (attrLength <= 0 || attrOffset + attrLength > record.Length)
                break;

            var nonResident = record[attrOffset + 0x08] != 0;
            var contentOffset = BitConverter.ToUInt16(record, attrOffset + 0x14);

            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                {
                    var b = attrOffset + contentOffset;
                    // $STANDARD_INFORMATION: last-modified is at +0x08 (FILETIME).
                    if (b + 16 <= record.Length)
                        lastWrite = FileTimeToUtc(BitConverter.ToInt64(record, b + 0x08));
                    break;
                }
                case AttrFileName when !nonResident:
                {
                    var b = attrOffset + contentOffset;
                    // $FILE_NAME: parent ref (+0x00, 6 bytes used), real size (+0x30),
                    // name length in chars (+0x40), namespace (+0x41), name (+0x42).
                    if (b + 0x42 > record.Length)
                        break;
                    var parentRef = BitConverter.ToUInt64(record, b + 0x00) & 0x0000FFFFFFFFFFFF;
                    var nsByte = record[b + 0x41];
                    var nameLen = record[b + 0x40];
                    // Namespace 2 = DOS short name (e.g. PROGRA~1); prefer the long
                    // name, so only take this name if we don't already have one.
                    var thisName = System.Text.Encoding.Unicode.GetString(record, b + 0x42, nameLen * 2);
                    fileNameSize = BitConverter.ToInt64(record, b + 0x30);
                    parentFrn = parentRef;
                    if (name is null || nsByte != 2)
                        name = thisName;
                    break;
                }
                case AttrData:
                {
                    // The authoritative size. Non-resident: "real size" is at +0x30
                    // of the attribute header. Resident: the content length at +0x10.
                    if (nonResident)
                    {
                        if (attrOffset + 0x38 <= record.Length)
                            dataSize = BitConverter.ToInt64(record, attrOffset + 0x30);
                    }
                    else
                    {
                        dataSize = BitConverter.ToUInt32(record, attrOffset + 0x10);
                    }
                    break;
                }
            }

            attrOffset += attrLength;
        }

        if (name is null)
            return null; // no usable name — skip

        var size = dataSize > 0 ? dataSize : fileNameSize;
        return new MftRecord(
            FileReferenceNumber: ownFrn,
            ParentFileReferenceNumber: parentFrn,
            Name: name,
            IsDirectory: isDir,
            SizeBytes: isDir ? 0 : size,
            LastWriteUtc: isDir ? default : lastWrite);
    }

    /// <summary>
    /// Restore the bytes NTFS replaced with the update-sequence check value. The
    /// record header points to an array: element 0 is the check value written into
    /// the last 2 bytes of each sector; elements 1..n are the original bytes to put
    /// back. Returns false if the structure is inconsistent (corrupt record).
    /// </summary>
    private static bool ApplyFixup(byte[] record, int bytesPerSector)
    {
        var usaOffset = BitConverter.ToUInt16(record, UpdateSeqOffset);
        var usaCount = BitConverter.ToUInt16(record, UpdateSeqSizeOffset);
        if (usaCount == 0)
            return true; // nothing to fix

        if (usaOffset + usaCount * 2 > record.Length)
            return false;

        // usaCount includes the check value itself, so there are (usaCount-1) sectors.
        var checkValue = BitConverter.ToUInt16(record, usaOffset);
        for (var i = 1; i < usaCount; i++)
        {
            var sectorEnd = i * bytesPerSector - 2;
            if (sectorEnd + 2 > record.Length)
                break;
            // The 2 bytes at each sector's end must currently equal the check value.
            if (BitConverter.ToUInt16(record, sectorEnd) != checkValue)
                return false; // corrupt / not a valid record
            // Restore the originals from the array.
            record[sectorEnd] = record[usaOffset + i * 2];
            record[sectorEnd + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    private static DateTime FileTimeToUtc(long fileTime)
    {
        if (fileTime <= 0)
            return default;
        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch { return default; }
    }
}
