namespace Reclaim.Core.Scanning;

/// <summary>One fragment of a non-resident attribute: a run of clusters on disk.</summary>
public readonly record struct DataRun(long StartCluster, long ClusterCount);

/// <summary>
/// Decodes an NTFS "data run list" — the compact, variable-length encoding NTFS
/// uses to describe where a non-resident attribute's content lives on disk. The
/// MFT itself is non-resident and usually fragmented, so to read the whole MFT we
/// must decode the run list from its $DATA attribute and read each fragment.
///
/// Encoding: each run starts with a header byte. The low nibble is the number of
/// bytes that follow giving the run's length (in clusters); the high nibble is the
/// number of bytes giving the starting cluster, encoded as a SIGNED delta from the
/// previous run's start (the first run's offset is absolute). A header byte of 0
/// ends the list.
///
/// Pure logic — unit-tested with synthetic run lists; no disk access here.
/// </summary>
public static class NtfsDataRunDecoder
{
    public static List<DataRun> Decode(byte[] runList, int offset = 0)
    {
        var runs = new List<DataRun>();
        long currentCluster = 0; // running absolute start; deltas accumulate onto it
        var i = offset;

        while (i < runList.Length)
        {
            var header = runList[i++];
            if (header == 0)
                break; // end of run list

            int lengthBytes = header & 0x0F;
            int offsetBytes = (header >> 4) & 0x0F;

            if (lengthBytes == 0 || i + lengthBytes + offsetBytes > runList.Length)
                break; // malformed

            // Length is an unsigned little-endian integer of lengthBytes bytes.
            long runLength = ReadUnsigned(runList, i, lengthBytes);
            i += lengthBytes;

            // Offset is a SIGNED little-endian delta. offsetBytes == 0 means a sparse
            // run (no on-disk location) — rare for $MFT, but handle it.
            if (offsetBytes == 0)
            {
                runs.Add(new DataRun(StartCluster: -1, ClusterCount: runLength)); // sparse
            }
            else
            {
                long delta = ReadSigned(runList, i, offsetBytes);
                i += offsetBytes;
                currentCluster += delta;
                runs.Add(new DataRun(currentCluster, runLength));
            }
        }

        return runs;
    }

    private static long ReadUnsigned(byte[] data, int offset, int count)
    {
        long value = 0;
        for (var b = 0; b < count; b++)
            value |= (long)data[offset + b] << (8 * b);
        return value;
    }

    private static long ReadSigned(byte[] data, int offset, int count)
    {
        long value = ReadUnsigned(data, offset, count);
        // Sign-extend from the top bit of the highest byte read.
        var signBit = 1L << (8 * count - 1);
        if ((value & signBit) != 0)
            value -= 1L << (8 * count);
        return value;
    }
}
